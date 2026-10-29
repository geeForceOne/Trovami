namespace Trovami.Core.Backup;

// Where everything persistent lives; all under one data directory (a Docker volume) by default.
public sealed record StoragePaths(string DataDirectory, string DatabasePath, string KeysPath, string BackupsPath, string PhotosPath)
{
    // A restore waits here until the next start, when nothing has the database open.
    public string PendingRestorePath => Path.Combine(DataDirectory, "restore-pending");

    // Uploaded backups are checked here before they're used.
    public string StagingPath => Path.Combine(DataDirectory, "restore-staging");

    public static StoragePaths From(string dataDirectory, string? databasePath, string? keysPath, string? backupsPath, string? photosPath, string contentRoot)
    {
        var data = Path.GetFullPath(dataDirectory, contentRoot);
        return new StoragePaths(
            data,
            Path.GetFullPath(databasePath ?? Path.Combine(data, "trovami.db"), contentRoot),
            Path.GetFullPath(keysPath ?? Path.Combine(data, "keys"), contentRoot),
            Path.GetFullPath(backupsPath ?? Path.Combine(data, "backups"), contentRoot),
            Path.GetFullPath(photosPath ?? Path.Combine(data, "photos"), contentRoot));
    }

    public void CreateDirectories()
    {
        foreach (var dir in new[] { DataDirectory, Path.GetDirectoryName(DatabasePath)!, KeysPath, BackupsPath, PhotosPath })
            Directory.CreateDirectory(dir);
    }
}
