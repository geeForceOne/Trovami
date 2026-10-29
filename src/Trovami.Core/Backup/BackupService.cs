using System.IO.Compression;
using System.Text.Json;
using Trovami.Core.Data;
using Trovami.Core.Localization;
using Trovami.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Trovami.Core.Backup;

public sealed record BackupFile(string Name, long Size, DateTime CreatedUtc, bool IsAutomatic);

public sealed record BackupSummary(DateTime CreatedUtc, string AppVersion, int Users, int Households, int Items, int Photos);

public sealed record BackupSettingsView(bool AutoBackups, TimeOnly Time, int Keep, string TimeZone, DateTime? LastAutoBackupAt, string? LastAutoBackupError);

// Backups of the whole instance: the database, the Data Protection keys (without them the saved
// SMTP password and the households' AI keys can't be read) and every photo, zipped with a small
// description. Automatic ones go to the backups folder; downloads can be password-protected. A
// restore is checked first, then applied on the next start, when nothing has the database open
// (the app restarts itself for it).
public sealed class BackupService(
    IDbContextFactory<AppDbContext> dbFactory, StoragePaths paths, TimeProvider time, ILogger<BackupService> logger)
{
    public const string AutoPrefix = "auto-";
    public const string SafetyPrefix = "before-restore-";
    public const string UpdatePrefix = "before-update-";
    public const int MaxKeep = 365;
    private const int SafetyKeep = 5;
    private const string DbEntry = "trovami.db";
    private const string KeysEntry = "keys/";
    private const string PhotosEntry = "photos/";
    private const string InfoEntry = "backup.json";
    private const string ReadyMarker = "ready";

    private sealed record BackupInfo(int Format, string AppVersion, DateTime CreatedUtc);

    private readonly SemaphoreSlim _lock = new(1, 1);

    // A zip of the current state for download, as a temporary file the caller deletes after
    // sending it. Encrypted when a password is given.
    public async Task<(string FileName, string TempPath)> CreateDownloadAsync(string? password, CancellationToken ct = default)
    {
        Directory.CreateDirectory(TempPath);
        var path = Path.Combine(TempPath, $"download-{Guid.NewGuid():N}.tmp");
        await CreateZipAsync(path, ct);
        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd_HHmm");
        if (string.IsNullOrEmpty(password))
            return ($"trovami-backup-{stamp}.zip", path);
        var encrypted = BackupCrypto.Encrypt(await File.ReadAllBytesAsync(path, ct), password);
        await File.WriteAllBytesAsync(path, encrypted, ct);
        return ($"trovami-backup-{stamp}.zip.aes", path);
    }

    // Downloads being prepared; anything left here is from an interrupted one.
    private string TempPath => Path.Combine(paths.DataDirectory, "backup-temp");

    // Removes leftovers of downloads that didn't finish.
    public void CleanTemp()
    {
        if (!Directory.Exists(TempPath))
            return;
        foreach (var f in new DirectoryInfo(TempPath).GetFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
            TryDelete(f.FullName);
    }

    // Writes a backup into the backups folder and removes the oldest beyond `keep` with the same
    // prefix. suffix goes after the time stamp (e.g. the version an update is going to).
    public async Task<BackupFile> CreateStoredAsync(string prefix, int keep, CancellationToken ct = default, string? suffix = null)
    {
        var name = $"{prefix}{time.GetUtcNow().UtcDateTime:yyyy-MM-dd_HHmmss}{suffix}.zip";
        Directory.CreateDirectory(paths.BackupsPath);
        var path = Path.Combine(paths.BackupsPath, name);
        var temp = path + ".tmp";
        try
        {
            await CreateZipAsync(temp, ct);
            File.Move(temp, path);
        }
        finally
        {
            TryDelete(temp);
        }
        foreach (var old in List().Where(b => b.Name.StartsWith(prefix, StringComparison.Ordinal)).Skip(keep))
            TryDelete(Path.Combine(paths.BackupsPath, old.Name));
        return ToFile(new FileInfo(path));
    }

    // The stored backups, newest first.
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(paths.BackupsPath))
            return [];
        return new DirectoryInfo(paths.BackupsPath).GetFiles("*.zip")
            .Select(ToFile).OrderByDescending(f => f.CreatedUtc).ToList();
    }

    // The full path of a stored backup, or null when there's no such file (or the name tries to
    // leave the folder).
    public string? PathOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.Combine(paths.BackupsPath, name);
        return File.Exists(path) ? path : null;
    }

    public void Delete(string name)
    {
        if (PathOf(name) is { } path)
            File.Delete(path);
    }

    // Checks an uploaded or stored backup and keeps it ready for ScheduleRestoreAsync. Throws
    // InputException with a user-facing reason when it can't be used.
    public async Task<BackupSummary> StageAsync(string backupPath, string? password, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ResetDirectory(paths.StagingPath);
            Stream source = File.OpenRead(backupPath);
            var head = new byte[32];
            var read = await source.ReadAsync(head, ct);
            source.Position = 0;
            if (BackupCrypto.IsEncrypted(head.AsSpan(0, read)))
            {
                if (string.IsNullOrEmpty(password))
                    throw new InputException("This backup is password-protected. Enter its password.");
                using var memory = new MemoryStream();
                await source.CopyToAsync(memory, ct);
                await source.DisposeAsync();
                source = new MemoryStream(BackupCrypto.Decrypt(memory.ToArray(), password));
            }

            BackupInfo info;
            var photoCount = 0;
            try
            {
                await using var _ = source;
                using var zip = new ZipArchive(source, ZipArchiveMode.Read);
                var infoEntry = zip.GetEntry(InfoEntry) ?? throw new InputException($"This isn't a {AppInfo.Name} backup.");
                await using (var s = infoEntry.Open())
                    info = await JsonSerializer.DeserializeAsync<BackupInfo>(s, cancellationToken: ct) ?? throw new InputException($"This isn't a {AppInfo.Name} backup.");
                var dbEntry = zip.GetEntry(DbEntry) ?? throw new InputException("The backup has no database.");
                dbEntry.ExtractToFile(Path.Combine(paths.StagingPath, DbEntry));
                var keysDir = Directory.CreateDirectory(Path.Combine(paths.StagingPath, "keys"));
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(KeysEntry, StringComparison.Ordinal) && e.Name.Length > 0))
                    e.ExtractToFile(Path.Combine(keysDir.FullName, Path.GetFileName(e.Name)));

                // photos/<household>/<file>; anything else under photos/ is ignored.
                var photosDir = Directory.CreateDirectory(Path.Combine(paths.StagingPath, "photos"));
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(PhotosEntry, StringComparison.Ordinal) && e.Name.Length > 0))
                {
                    var parts = e.FullName[PhotosEntry.Length..].Split('/');
                    if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p != Path.GetFileName(p) || p.StartsWith('.')))
                        continue;
                    var dir = Directory.CreateDirectory(Path.Combine(photosDir.FullName, parts[0]));
                    e.ExtractToFile(Path.Combine(dir.FullName, parts[1]));
                    if (!parts[1].Contains(".thumb."))
                        photoCount++;
                }
            }
            catch (InvalidDataException)
            {
                throw new InputException("This file isn't a backup, or it's damaged.");
            }

            var stagedDb = Path.Combine(paths.StagingPath, DbEntry);
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={stagedDb};Mode=ReadOnly;Pooling=False").Options;
            await using (var staged = new AppDbContext(options))
            {
                IReadOnlyList<string> applied;
                try
                {
                    applied = (await staged.Database.GetAppliedMigrationsAsync(ct)).ToList();
                }
                catch (SqliteException)
                {
                    throw new InputException("The backup's database can't be read.");
                }
                var known = staged.Database.GetMigrations().ToHashSet();
                if (applied.Any(m => !known.Contains(m)))
                    throw new InputException($"This backup comes from a newer version of {AppInfo.Name} ({info.AppVersion}). Update the app first, then restore it.");
                var summary = new BackupSummary(info.CreatedUtc, info.AppVersion,
                    await staged.Users.CountAsync(ct), await staged.Households.CountAsync(ct), await staged.Items.CountAsync(ct), photoCount);
                return summary;
            }
        }
        catch
        {
            TryDeleteDirectory(paths.StagingPath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Makes a safety backup of the current state, then hands the staged backup to the next start.
    // The caller stops the app afterwards.
    public async Task ScheduleRestoreAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(paths.StagingPath, DbEntry)))
            throw new InputException("Check a backup first.");
        await CreateStoredAsync(SafetyPrefix, SafetyKeep, ct);
        await _lock.WaitAsync(ct);
        try
        {
            TryDeleteDirectory(paths.PendingRestorePath);
            Directory.Move(paths.StagingPath, paths.PendingRestorePath);
            await File.WriteAllTextAsync(Path.Combine(paths.PendingRestorePath, ReadyMarker), time.GetUtcNow().ToString("O"), ct);
            logger.LogWarning("A restore is ready and will be applied on the next start");
        }
        finally
        {
            _lock.Release();
        }
    }

    public void DiscardStaged() => TryDeleteDirectory(paths.StagingPath);

    // Called at startup before the database schema is updated: a backup of the database as it is,
    // so an update that goes wrong can be undone. Throws when it can't be made; the caller then
    // doesn't update.
    public Task<BackupFile> CreateBeforeUpdateAsync(CancellationToken ct = default) =>
        CreateStoredAsync(UpdatePrefix, SafetyKeep, ct, "-" + AppInfo.Version);

    // Called once at startup, before the database or the key ring is opened: swaps in a restore
    // that ScheduleRestoreAsync prepared. Returns true when one was applied.
    public static bool ApplyPendingRestore(StoragePaths paths, ILogger? log = null)
    {
        var pending = paths.PendingRestorePath;
        if (!File.Exists(Path.Combine(pending, ReadyMarker)) || !File.Exists(Path.Combine(pending, DbEntry)))
        {
            TryDeleteDirectory(pending);
            return false;
        }

        // The previous process may still be letting go of the files for a moment.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            for (var attempt = 1; File.Exists(paths.DatabasePath + suffix); attempt++)
            {
                try { File.Delete(paths.DatabasePath + suffix); }
                catch (IOException) when (attempt < 30) { Thread.Sleep(500); }
            }
        File.Move(Path.Combine(pending, DbEntry), paths.DatabasePath);

        var stagedKeys = Path.Combine(pending, "keys");
        if (Directory.Exists(stagedKeys) && Directory.EnumerateFiles(stagedKeys).Any())
        {
            Directory.CreateDirectory(paths.KeysPath);
            foreach (var f in Directory.EnumerateFiles(paths.KeysPath))
                File.Delete(f);
            foreach (var f in Directory.EnumerateFiles(stagedKeys))
                File.Move(f, Path.Combine(paths.KeysPath, Path.GetFileName(f)));
        }

        var stagedPhotos = Path.Combine(pending, "photos");
        if (Directory.Exists(stagedPhotos))
        {
            TryDeleteDirectory(paths.PhotosPath);
            Directory.Move(stagedPhotos, paths.PhotosPath);
        }
        TryDeleteDirectory(pending);
        log?.LogWarning("Restored the database, keys and photos from a backup");
        return true;
    }

    public async Task<BackupSettingsView> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct) ?? new SiteSettings();
        return new BackupSettingsView(s.AutoBackups, s.BackupTime, s.BackupKeep, s.BackupTimeZone, s.LastAutoBackupAt, s.LastAutoBackupError);
    }

    public async Task SaveSettingsAsync(bool auto, TimeOnly at, int keep, string timeZone, CancellationToken ct = default)
    {
        if (keep is < 1 or > MaxKeep)
            throw new InputException($"Keep between 1 and {MaxKeep} backups.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
            db.SiteSettings.Add(s = new SiteSettings());
        s.AutoBackups = auto;
        s.BackupTime = at;
        s.BackupKeep = keep;
        s.BackupTimeZone = Regions.IsKnownTimeZone(timeZone) ? timeZone : Regions.DefaultTimeZone;
        await db.SaveChangesAsync(ct);
    }

    // The scheduler's job: one automatic backup a day at the set time, and one later that day if
    // the app was down at that time.
    public async Task<bool> RunDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
            db.SiteSettings.Add(s = new SiteSettings());
        if (!s.AutoBackups)
            return false;

        var utcNow = time.GetUtcNow().UtcDateTime;
        var localNow = Regions.LocalNow(utcNow, s.BackupTimeZone);
        var dueToday = DateOnly.FromDateTime(localNow).ToDateTime(s.BackupTime);
        var last = s.LastAutoBackupAt is { } l ? Regions.LocalNow(l, s.BackupTimeZone) : (DateTime?)null;
        if (localNow < dueToday || last >= dueToday)
            return false;

        try
        {
            await CreateStoredAsync(AutoPrefix, s.BackupKeep, ct);
            s.LastAutoBackupError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            logger.LogError(ex, "The automatic backup failed");
            s.LastAutoBackupError = ex.Message;
        }
        s.LastAutoBackupAt = utcNow;
        await db.SaveChangesAsync(ct);
        return s.LastAutoBackupError is null;
    }

    // Writes the zip straight to a file: with photos, a backup can be far larger than memory
    // should hold.
    private async Task CreateZipAsync(string target, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var copy = Path.Combine(paths.DataDirectory, $"backup-{Guid.NewGuid():N}.tmp");
        try
        {
            // VACUUM INTO writes a consistent copy while the app keeps using the database.
            await using (var conn = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM INTO $path";
                cmd.Parameters.AddWithValue("$path", copy);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using var output = File.Create(target);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(copy, DbEntry, CompressionLevel.Optimal);
            if (Directory.Exists(paths.KeysPath))
                foreach (var key in Directory.EnumerateFiles(paths.KeysPath, "*.xml"))
                    zip.CreateEntryFromFile(key, KeysEntry + Path.GetFileName(key));
            // JPEGs don't compress any further.
            if (Directory.Exists(paths.PhotosPath))
                foreach (var dir in Directory.EnumerateDirectories(paths.PhotosPath))
                    foreach (var photo in Directory.EnumerateFiles(dir, "*.jpg"))
                    {
                        ct.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(photo, $"{PhotosEntry}{Path.GetFileName(dir)}/{Path.GetFileName(photo)}", CompressionLevel.NoCompression);
                    }
            var info = new BackupInfo(1, AppInfo.Version, time.GetUtcNow().UtcDateTime);
            await using var s = zip.CreateEntry(InfoEntry).Open();
            await JsonSerializer.SerializeAsync(s, info, cancellationToken: ct);
        }
        finally
        {
            TryDelete(copy);
        }
    }

    private static BackupFile ToFile(FileInfo f) =>
        new(f.Name, f.Length, f.LastWriteTimeUtc, f.Name.StartsWith(AutoPrefix, StringComparison.Ordinal));

    private static void ResetDirectory(string dir)
    {
        TryDeleteDirectory(dir);
        Directory.CreateDirectory(dir);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
