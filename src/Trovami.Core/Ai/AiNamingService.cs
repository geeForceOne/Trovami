using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Photos;
using Trovami.Core.Security;
using Trovami.Core.Services;

namespace Trovami.Core.Ai;

// Items waiting to be named, in arrival order. The host drains it (see AiNamingService.RunAsync).
public sealed class AiNamingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid itemId) => _channel.Writer.TryWrite(itemId);

    internal ChannelReader<Guid> Reader => _channel.Reader;
}

public sealed record AiSettingsView(AiProvider Provider, string Model, SecretStatus Key);

// A developer's own key for local test instances (see Program.cs); never set in production.
public sealed record DevAiKey(AiProvider Provider, string Key);

// Names items from their photos with the household's own AI key. Runs on the server as soon as a
// photo arrives, so photos taken offline are named when the phone syncs.
public sealed class AiNamingService(
    IDbContextFactory<AppDbContext> dbFactory, PhotoStore photos, AiNamingQueue queue, IDataProtectionProvider dataProtection,
    ILogger<AiNamingService> logger)
{
    private readonly SecretProtector _protector = new(dataProtection, "Trovami.HouseholdAiKey.v1");

    // Overridable in tests.
    internal Func<AiProvider, string, string, IAiNamer> NamerFactory { get; set; } = AiNamer.Create;

    // Development only: used for households without a working key of their own, so throwaway
    // instances can name items without entering a key each time.
    public DevAiKey? DevFallback { get; set; }

    public async Task<AiSettingsView> GetSettingsAsync(string userId, Guid householdId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var h = await Access.RequireOwnerAsync(db, userId, householdId, ct);
        return new AiSettingsView(h.AiProvider, h.AiModel ?? AiModels.DefaultFor(h.AiProvider), _protector.Read(h.AiKeyProtected).Status);
    }

    // newKey: null keeps the saved key, empty removes it. Turning naming on (or fixing the key)
    // retries the items that couldn't be named so far.
    public async Task SaveSettingsAsync(string userId, Guid householdId, AiProvider provider, string? model, string? newKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var h = await Access.RequireOwnerAsync(db, userId, householdId, ct);
        if (provider != h.AiProvider && newKey is null && provider != AiProvider.None)
            h.AiKeyProtected = null;
        h.AiProvider = provider;
        h.AiModel = provider == AiProvider.None ? null
            : AiModels.For(provider).Any(m => m.Id == model) ? model : AiModels.DefaultFor(provider);
        if (newKey is not null)
            h.AiKeyProtected = newKey.Trim().Length > 0 ? _protector.Protect(newKey.Trim()) : null;
        if (provider == AiProvider.None)
            h.AiKeyProtected = null;
        await db.SaveChangesAsync(ct);

        if (h.AiProvider != AiProvider.None && h.AiKeyProtected is not null)
        {
            var failed = await db.Items
                .Where(i => i.HouseholdId == householdId && i.AiNaming == AiNaming.Failed && i.PhotoVersion != null
                    && ((i.Name == null && i.NameSource != NameSource.Person) || i.Keywords == null))
                .Select(i => i.Id).ToListAsync(ct);
            await db.Items.Where(i => failed.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.AiNaming, AiNaming.Pending), ct);
            foreach (var id in failed)
                queue.Enqueue(id);
        }
    }

    // The host's loop: first the items left pending by a restart, then whatever arrives.
    public async Task RunAsync(CancellationToken ct)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var pending = await db.Items.Where(i => i.AiNaming == AiNaming.Pending && i.PhotoVersion != null)
                .OrderBy(i => i.CreatedAt).Select(i => i.Id).ToListAsync(ct);
            foreach (var id in pending)
                queue.Enqueue(id);
        }
        await foreach (var id in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await NameAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Naming item {ItemId} failed", id);
            }
        }
    }

    // The AI has something to do: a name (unless a person typed or cleared one), or keywords.
    public static bool NeedsAi(Item item) => (item.Name is null && item.NameSource != NameSource.Person) || item.Keywords is null;

    internal async Task NameAsync(Guid itemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null || item.AiNaming != AiNaming.Pending || item.PhotoVersion is null)
            return;
        if (!NeedsAi(item))
        {
            await SetStateAsync(db, itemId, AiNaming.NotNeeded, new AiDescription(null, []), ct);
            return;
        }

        var household = await db.Households.AsNoTracking().FirstAsync(h => h.Id == item.HouseholdId, ct);
        var key = _protector.Read(household.AiKeyProtected);
        var provider = household.AiProvider;
        var apiKey = key.Status == SecretStatus.Set ? key.Value : null;
        var model = household.AiModel ?? AiModels.DefaultFor(provider);
        if ((provider == AiProvider.None || apiKey is null) && DevFallback is { } dev)
            (provider, apiKey, model) = (dev.Provider, dev.Key, AiModels.DefaultFor(dev.Provider));
        var jpeg = await photos.ReadAsync(item.HouseholdId, item.Id, ct);
        if (provider == AiProvider.None || apiKey is null || jpeg is null)
        {
            await SetStateAsync(db, itemId, AiNaming.Failed, new AiDescription(null, []), ct);
            return;
        }

        // The household's keywords so far, so the AI reuses them instead of adding near-doubles.
        IReadOnlyList<string> known = [];
        if (item.Keywords is null)
        {
            var stored = await db.Items.AsNoTracking()
                .Where(i => i.HouseholdId == item.HouseholdId && i.Keywords != null && i.Keywords != "")
                .Select(i => i.Keywords).ToListAsync(ct);
            known = stored.SelectMany(ItemKeywords.Split).GroupBy(k => k)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).Take(AiNamer.MaxKnownKeywords).ToList();
        }

        AiDescription seen;
        try
        {
            var namer = NamerFactory(provider, apiKey, model);
            seen = await namer.DescribeAsync(jpeg, known, ct);
        }
        catch (AiException ex)
        {
            logger.LogWarning("AI naming of item {ItemId} failed: {Reason}", itemId, ex.Message);
            seen = new AiDescription(null, []);
        }
        var named = seen.Name is not null || item.Name is not null || item.NameSource == NameSource.Person;
        var described = seen.Keywords.Count > 0 || item.Keywords is not null;
        await SetStateAsync(db, itemId, named && described ? AiNaming.NotNeeded : AiNaming.Failed, seen, ct);
    }

    // Each part only if nobody filled it in meanwhile: what a person typed always wins.
    private static async Task SetStateAsync(AppDbContext db, Guid itemId, AiNaming state, AiDescription seen, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (seen.Name is not null)
            await db.Items.Where(i => i.Id == itemId && i.Name == null && i.NameSource != NameSource.Person)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Name, seen.Name)
                    .SetProperty(i => i.NameSource, NameSource.Ai)
                    .SetProperty(i => i.UpdatedAt, now), ct);
        if (seen.Keywords.Count > 0)
        {
            var keywords = ItemKeywords.Join(seen.Keywords);
            await db.Items.Where(i => i.Id == itemId && i.Keywords == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Keywords, keywords).SetProperty(i => i.UpdatedAt, now), ct);
        }
        await db.Items.Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.AiNaming, state), ct);
    }
}
