using System.Net.Mail;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Services;

public sealed record AccountRequestView(int Id, string Email, string Name, string? Message, DateTime CreatedAt);

// Account requests from the sign-in page, when the admin allows them. This part only stores and
// lists them; approving (creating the account, sending the invite) and the emails live with the
// rest of the account handling in the web app.
public sealed class AccountRequestService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Unanswered requests are removed after this long.
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public async Task<bool> IsOpenAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SiteSettings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.AllowAccountRequests).FirstOrDefaultAsync(ct);
    }

    public async Task SetOpenAsync(bool open, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
        {
            s = new SiteSettings();
            db.SiteSettings.Add(s);
        }
        s.AllowAccountRequests = open;
        await db.SaveChangesAsync(ct);
    }

    // Whether household invites to addresses without an account create the account.
    public async Task<bool> InvitesCreateAccountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SiteSettings.AsNoTracking().Where(s => s.Id == 1).Select(s => (bool?)s.InvitesCreateAccounts).FirstOrDefaultAsync(ct) ?? true;
    }

    public async Task SetInvitesCreateAccountsAsync(bool allowed, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.SiteSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null)
            db.SiteSettings.Add(s = new SiteSettings());
        s.InvitesCreateAccounts = allowed;
        await db.SaveChangesAsync(ct);
    }

    // Validates and stores a request. Returns the stored request, or null when nothing new was
    // stored (the email already has an account or a pending request): the caller shows the same
    // "thanks" either way, so the form can't be used to find out who has an account.
    public async Task<AccountRequestView?> SubmitAsync(string email, string name, string? message, CancellationToken ct = default)
    {
        var cleanEmail = email.Trim();
        var cleanName = name.Trim();
        var cleanMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (cleanEmail.Length == 0 || cleanEmail.Length > AccountRequest.MaxEmailLength || !MailAddress.TryCreate(cleanEmail, out _))
            throw new InputException("Enter a valid email address.");
        if (cleanName.Length == 0)
            throw new InputException("Enter your name.");
        if (cleanName.Length > AppUser.MaxDisplayNameLength)
            throw new InputException($"Keep the name under {AppUser.MaxDisplayNameLength} characters.");
        if (cleanMessage is { Length: > AccountRequest.MaxMessageLength })
            throw new InputException($"Keep the message under {AccountRequest.MaxMessageLength} characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.SiteSettings.AnyAsync(s => s.Id == 1 && s.AllowAccountRequests, ct))
            throw new InputException("Account requests aren't open. Ask the administrator for an invite.");

        await RemoveExpiredAsync(db, ct);
        var normalized = cleanEmail.ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized || u.NormalizedUserName == normalized, ct)
            || await db.AccountRequests.AnyAsync(r => r.Email.ToUpper() == normalized, ct))
            return null;

        var request = new AccountRequest { Email = cleanEmail, Name = cleanName, Message = cleanMessage };
        db.AccountRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return View(request);
    }

    public async Task<IReadOnlyList<AccountRequestView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await RemoveExpiredAsync(db, ct);
        return (await db.AccountRequests.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync(ct)).Select(View).ToList();
    }

    public async Task<AccountRequestView?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AccountRequests.AsNoTracking().Where(r => r.Id == id).Select(r => View(r)).FirstOrDefaultAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.AccountRequests.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    private static async Task RemoveExpiredAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - KeepFor;
        await db.AccountRequests.Where(r => r.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private static AccountRequestView View(AccountRequest r) => new(r.Id, r.Email, r.Name, r.Message, r.CreatedAt);
}
