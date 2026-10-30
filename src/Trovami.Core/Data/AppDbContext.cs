using Trovami.Core.Models;
using Trovami.Core.Services;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Household> Households => Set<Household>();
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();
    public DbSet<HouseholdInvite> HouseholdInvites => Set<HouseholdInvite>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<SyncCounter> SyncCounters => Set<SyncCounter>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<AccountRequest> AccountRequests => Set<AccountRequest>();

    // Gives every added or changed synced row (location, tag) the next version, in the
    // same transaction as the change. SQLite runs one writer at a time, so versions are handed out
    // in commit order and a phone asking for "everything after N" never misses a row.
    public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var synced = ChangeTracker.Entries<ISynced>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        if (synced.Count == 0)
            return await base.SaveChangesAsync(ct);

        var owns = Database.CurrentTransaction is null;
        await using var tx = owns ? await Database.BeginTransactionAsync(ct) : null;
        var next = await NextVersionsAsync(synced.Count, ct);
        var now = DateTime.UtcNow;
        foreach (var e in synced)
        {
            e.Entity.Version = next++;
            e.Entity.UpdatedAt = now;
        }
        var result = await base.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);
        return result;
    }

    public override int SaveChanges() =>
        throw new NotSupportedException("Use SaveChangesAsync, which sets the sync versions.");

    // Reserves count versions; returns the first.
    private async Task<long> NextVersionsAsync(int count, CancellationToken ct)
    {
        var last = await Database.SqlQuery<long>(
                $"UPDATE SyncCounters SET Value = Value + {count} WHERE Id = 1 RETURNING Value AS Value")
            .ToListAsync(ct);
        if (last.Count == 1)
            return last[0] - count + 1;
        SyncCounters.Add(new SyncCounter { Id = 1, Value = count });
        return 1;
    }

    // The latest version handed out; a phone that has seen everything up to it is up to date.
    public async Task<long> CurrentVersionAsync(CancellationToken ct = default) =>
        await SyncCounters.Where(c => c.Id == 1).Select(c => (long?)c.Value).FirstOrDefaultAsync(ct) ?? 0;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e => e.Property(x => x.DisplayName).HasMaxLength(AppUser.MaxDisplayNameLength));

        b.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserSettings>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Theme).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RegionFormat).HasMaxLength(16);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.HasIndex(x => x.HomepageTokenHash);
        });

        b.Entity<Household>(e =>
        {
            // Deleting the owner's account is refused while they own households (see UserAdminService).
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(Household.MaxNameLength);
            e.Property(x => x.AiProvider).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AiModel).HasMaxLength(100);
        });

        b.Entity<HouseholdMember>(e =>
        {
            e.HasKey(x => new { x.HouseholdId, x.UserId });
            e.HasOne(x => x.Household).WithMany(h => h.Members).HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<HouseholdInvite>(e =>
        {
            e.HasOne(x => x.Household).WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.InvitedById).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.HouseholdId }).IsUnique();
        });

        b.Entity<Location>(e =>
        {
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: a location is only deleted once it's empty (see LocationService).
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(Location.MaxNameLength);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Notes).HasMaxLength(Location.MaxNotesLength);
            e.Property(x => x.Color).HasMaxLength(16);
            e.Property(x => x.Label).HasMaxLength(Location.MaxLabelLength);
            e.Property(x => x.PhotoVersion).HasMaxLength(16);
            e.Property(x => x.ShortCode).HasMaxLength(ShortCodes.Length);
            e.HasIndex(x => x.ShortCode).IsUnique();
            e.HasIndex(x => new { x.HouseholdId, x.ParentId });
            e.HasIndex(x => x.Version);
            e.HasQueryFilter(x => x.DeletedAt == null);
        });

        b.Entity<Item>(e =>
        {
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Name).HasMaxLength(Item.MaxNameLength);
            e.Property(x => x.NameSource).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AiNaming).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Notes).HasMaxLength(Item.MaxNotesLength);
            e.Property(x => x.PhotoVersion).HasMaxLength(16);
            e.Property(x => x.ShortCode).HasMaxLength(ShortCodes.Length);
            e.HasIndex(x => x.ShortCode).IsUnique();
            e.HasIndex(x => new { x.HouseholdId, x.LocationId });
            e.HasIndex(x => x.AiNaming);
        });

        b.Entity<Tag>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => x.LocationId);
            e.HasIndex(x => x.Version);
            e.HasQueryFilter(x => x.DeletedAt == null);
        });

        b.Entity<Device>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(Device.MaxNameLength);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.AppVersion).HasMaxLength(32);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<PairingCode>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => x.CodeHash).IsUnique();
        });

        b.Entity<SyncCounter>(e => e.Property(x => x.Id).ValueGeneratedNever());

        b.Entity<SiteSettings>(e => e.Property(x => x.Id).ValueGeneratedNever());

        b.Entity<AccountRequest>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(AccountRequest.MaxEmailLength);
            e.Property(x => x.Name).HasMaxLength(AppUser.MaxDisplayNameLength);
            e.Property(x => x.Message).HasMaxLength(AccountRequest.MaxMessageLength);
        });
    }
}
