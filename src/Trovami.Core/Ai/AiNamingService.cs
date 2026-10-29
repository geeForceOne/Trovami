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

// Names items from their photos with the household's own AI key. Runs on the server as soon as a
// photo arrives, so photos taken offline are named when the phone syncs.
public sealed class AiNamingService(
    IDbContextFactory<AppDbContext> dbFactory, PhotoStore photos, AiNamingQueue queue, IDataProtectionProvider dataProtection,
    ILogger<AiNamingService> logger)
{
    private readonly SecretProtector _protector = new(dataProtection, "Trovami.HouseholdAiKey.v1");

    // Overridable in tests.
    internal Func<AiProvider, string, string, IAiNamer> NamerFactory { get; set; } = AiNamer.Create;

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
                .Where(i => i.HouseholdId == householdId && i.AiNaming == AiNaming.Failed && i.Name == null && i.NameSource != NameSource.Person && i.PhotoVersion != null)
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

    internal async Task NameAsync(Guid itemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null || item.AiNaming != AiNaming.Pending || item.PhotoVersion is null)
            return;
        if (item.Name is not null || item.NameSource == NameSource.Person)
        {
            await SetStateAsync(db, itemId, AiNaming.NotNeeded, null, ct);
            return;
        }

        var household = await db.Households.AsNoTracking().FirstAsync(h => h.Id == item.HouseholdId, ct);
        var key = _protector.Read(household.AiKeyProtected);
        var jpeg = await photos.ReadAsync(item.HouseholdId, item.Id, ct);
        if (household.AiProvider == AiProvider.None || key.Status != SecretStatus.Set || jpeg is null)
        {
            await SetStateAsync(db, itemId, AiNaming.Failed, null, ct);
            return;
        }

        string? name;
        try
        {
            var namer = NamerFactory(household.AiProvider, key.Value!, household.AiModel ?? AiModels.DefaultFor(household.AiProvider));
            name = await namer.NameAsync(jpeg, ct);
        }
        catch (AiException ex)
        {
            logger.LogWarning("AI naming of item {ItemId} failed: {Reason}", itemId, ex.Message);
            name = null;
        }
        await SetStateAsync(db, itemId, name is null ? AiNaming.Failed : AiNaming.NotNeeded, name, ct);
    }

    // Only if nobody typed a name in the meantime: a person's name always wins.
    private static Task SetStateAsync(AppDbContext db, Guid itemId, AiNaming state, string? name, CancellationToken ct) =>
        db.Items.Where(i => i.Id == itemId && i.Name == null && i.NameSource != NameSource.Person)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.AiNaming, state)
                .SetProperty(i => i.Name, name)
                .SetProperty(i => i.NameSource, name == null ? NameSource.None : NameSource.Ai)
                .SetProperty(i => i.UpdatedAt, DateTime.UtcNow), ct);
}
