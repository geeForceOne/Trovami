using Microsoft.AspNetCore.Identity;

namespace Trovami.Core.Models;

public sealed class AppUser : IdentityUser
{
    public const int MaxDisplayNameLength = 80;

    // Required for new accounts; older or odd ones fall back to the first part of the email address.
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastActiveAt { get; set; }

    // The last version whose "what's new" banner this person closed (see WhatsNewService).
    public string? SeenVersion { get; set; }

    // The household the web app shows; null or one they left: their first one.
    public Guid? CurrentHouseholdId { get; set; }

    // Admin notifications; only used while the person is an admin.
    public bool NotifyInviteAccepted { get; set; } = true;
    public bool NotifyAccountRequests { get; set; } = true;

    public string ShownName => ShownNameFor(DisplayName, Email, UserName);

    public static string ShownNameFor(string? displayName, string? email, string? userName) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim()
        : !string.IsNullOrWhiteSpace(email) ? email.Split('@')[0]
        : userName ?? "?";
}

// One row per user; created on first use with values from the browser.
public sealed class UserSettings
{
    public string UserId { get; set; } = "";
    public Theme Theme { get; set; } = Theme.Dark;

    // Number and date formats, e.g. "en-US". The UI is always in English.
    public string RegionFormat { get; set; } = Localization.Regions.DefaultRegion;

    // IANA id; times (last active, backups) are shown in it.
    public string TimeZone { get; set; } = "Europe/Zurich";

    // SHA-256 (hex) of the user's homepage widget token; the token itself is shown once.
    public string? HomepageTokenHash { get; set; }
    public DateTime? HomepageTokenCreatedAt { get; set; }
}

// Rows the phone keeps a copy of (locations and tags). Version comes from one counter for the whole database
// (AppDbContext sets it on every save), so "everything changed since version N" is one query.
// Deleting one only sets DeletedAt, so phones learn about it on their next sync.
public interface ISynced
{
    long Version { get; set; }
    DateTime UpdatedAt { get; set; }
    DateTime? DeletedAt { get; set; }
}

// Everything in Trovami belongs to a household. Its creator is the owner: manages members, the AI
// key, and can delete it; members see and edit everything in it.
// Phones get the full list of their households on every sync, so a household needs no version:
// one that's gone is simply missing from the list.
public sealed class Household
{
    public const int MaxNameLength = 80;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Naming items from their photo. The key is encrypted with Data Protection and never leaves
    // the server.
    public AiProvider AiProvider { get; set; } = AiProvider.None;
    public string? AiModel { get; set; }
    public string? AiKeyProtected { get; set; }

    public List<HouseholdMember> Members { get; set; } = [];
}

public sealed class HouseholdMember
{
    public Guid HouseholdId { get; set; }
    public Household? Household { get; set; }
    public string UserId { get; set; } = "";
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

// An invite of an existing user into a household, waiting for them to accept or decline it.
// (A new address gets an account invite instead and joins right away.)
public sealed class HouseholdInvite
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public int Id { get; set; }
    public Guid HouseholdId { get; set; }
    public Household? Household { get; set; }
    public string UserId { get; set; } = "";
    public string InvitedById { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A place in the household's tree: a house, a room, a drawer, a box. Kind only picks the icon.
public sealed class Location : ISynced
{
    public const int MaxNameLength = 80;
    public const int MaxLabelLength = 16;
    public const int MaxNotesLength = 2000;

    // Created on the phone too (linking a tag offline), so the client picks the id.
    public Guid Id { get; set; } = Guid.NewGuid();

    // For the web app's addresses (see ShortCodes); given by the server.
    public string ShortCode { get; set; } = "";
    public Guid HouseholdId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public LocationKind Kind { get; set; } = LocationKind.Other;
    public string? Notes { get; set; }

    // A key from LocationColors.
    public string? Color { get; set; }

    // What's written on the box, e.g. "B12".
    public string? Label { get; set; }

    // Set when it has a photo: photos/<household>/<id>.jpg (see PhotoStore); changes with each new
    // photo, so browsers don't keep showing the old one.
    public string? PhotoVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}

// A thing stored somewhere: at one location, or in the household's Inbox (LocationId null).
public sealed class Item
{
    public const int MaxNameLength = 120;
    public const int MaxNotesLength = 2000;
    public const int MaxQuantity = 1_000_000;
    public const int MaxKeywords = 20;
    public const int MaxKeywordLength = 40;

    // Created on the phone, possibly offline, so the client picks the id; uploading it twice is
    // harmless.
    public Guid Id { get; set; } = Guid.NewGuid();

    // For the web app's addresses (see ShortCodes); given by the server.
    public string ShortCode { get; set; } = "";
    public Guid HouseholdId { get; set; }
    public Guid? LocationId { get; set; }

    // Null: "Unnamed item" (waiting for the AI, or for a person).
    public string? Name { get; set; }
    public NameSource NameSource { get; set; } = NameSource.None;
    public AiNaming AiNaming { get; set; } = AiNaming.NotNeeded;
    public string? Notes { get; set; }
    public int Quantity { get; set; } = 1;

    // Words Find also matches (color, material, kind of thing), comma-separated. Null: not set yet,
    // so the AI fills them in from the photo; once set, by the AI or a person, the AI leaves them.
    public string? Keywords { get; set; }

    // Set once the photo has arrived; see Location.PhotoVersion.
    public string? PhotoVersion { get; set; }

    // The phone said a photo is coming; until then the item shows a placeholder.
    public bool PhotoExpected { get; set; }
    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// An NFC tag or a printed QR label: a random code, linked to one location. Both carry
// trovami://t/<code>.
public sealed class Tag : ISynced
{
    public const int CodeLength = 12;

    // Made by the phone (NFC) or the web app (QR labels).
    public string Code { get; set; } = "";
    public Guid HouseholdId { get; set; }
    public Guid LocationId { get; set; }
    public TagKind Kind { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}

// A phone signed in to a user's account. Only the token's hash is stored.
public sealed class Device
{
    public const int MaxNameLength = 80;

    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? AppVersion { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
}

// A one-time code from Profile's "Connect a phone" QR code. Only the hash is stored.
public sealed class PairingCode
{
    public static readonly TimeSpan ValidFor = TimeSpan.FromMinutes(10);

    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

// The counter Version values come from; a single row.
public sealed class SyncCounter
{
    public int Id { get; set; } = 1;
    public long Value { get; set; }
}

public sealed class EmailSettings
{
    public int Id { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public EmailSecurity Security { get; set; } = EmailSecurity.StartTls;
    public string? UserName { get; set; }
    public string? PasswordProtected { get; set; }
    public string? From { get; set; }
    public string? FromName { get; set; }

    // The app's public address for links in emails, e.g. https://trovami.example.com.
    public string? PublicUrl { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Instance-wide settings; a single row with Id 1.
public sealed class SiteSettings
{
    public int Id { get; set; } = 1;
    public bool AllowAccountRequests { get; set; }

    // Household invites to addresses without an account create the account. Off: only the admin
    // creates accounts, and such invites are refused.
    public bool InvitesCreateAccounts { get; set; } = true;

    public bool AutoBackups { get; set; } = true;
    public TimeOnly BackupTime { get; set; } = new(3, 0);
    public int BackupKeep { get; set; } = 14;

    // The time zone BackupTime is in: the one of the admin who last saved the backup settings.
    public string BackupTimeZone { get; set; } = "Europe/Zurich";
    public DateTime? LastAutoBackupAt { get; set; }
    public string? LastAutoBackupError { get; set; }
}

public sealed class AccountRequest
{
    public const int MaxEmailLength = 256;
    public const int MaxMessageLength = 1000;

    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
