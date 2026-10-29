using System.ComponentModel.DataAnnotations;
using Trovami.Core.Data;
using Trovami.Core.Models;
using Trovami.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Trovami.Core.Email;

// What the admin's Email page shows. Carries no password, only whether one is saved.
public sealed record EmailSettingsView(
    string? Host,
    int Port,
    EmailSecurity Security,
    string? UserName,
    SecretStatus Password,
    string? From,
    string? FromName,
    string? PublicUrl)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

public sealed record EmailSettingsUpdate(
    string? Host,
    int Port,
    EmailSecurity Security,
    string? UserName,
    // null leaves the stored password unchanged; empty string removes it.
    string? NewPassword,
    string? From,
    string? FromName,
    string? PublicUrl);

// Everything needed to send, with the password decrypted. Only ever used server side.
public sealed record EmailConnection(
    string Host, int Port, EmailSecurity Security, string? UserName, string? Password,
    string From, string FromName, string? PublicUrl);

// The admin-edited mail settings. Kept in memory after the first read, and refreshed on save,
// since every sign-in page asks whether email is on.
public sealed class EmailSettingsService(IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection)
{
    public const string DefaultFromName = AppInfo.Name;

    private readonly SecretProtector _protector = new(dataProtection, "Trovami.SmtpPassword.v1");
    private EmailSettings? _cached;

    public async Task<EmailSettingsView> GetAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync(ct);
        return new EmailSettingsView(s.Host, s.Port, s.Security, s.UserName, _protector.Read(s.PasswordProtected).Status,
            s.From, s.FromName, s.PublicUrl);
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) => (await GetAsync(ct)).IsConfigured;

    // Null when email is off. Throws a user-facing error when the saved password can't be read.
    public async Task<EmailConnection?> GetConnectionAsync(CancellationToken ct = default)
    {
        var s = await LoadAsync(ct);
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.From))
            return null;

        var password = _protector.Read(s.PasswordProtected);
        if (password.Status == SecretStatus.Unreadable)
            throw new EmailException("The saved SMTP password can't be read anymore. Enter it again on the Admin settings page.");

        return new EmailConnection(s.Host, s.Port, s.Security, s.UserName, password.Value, s.From,
            string.IsNullOrWhiteSpace(s.FromName) ? DefaultFromName : s.FromName, s.PublicUrl);
    }

    // Returns the problems with the update; nothing is saved unless the list is empty.
    public async Task<IReadOnlyList<string>> SaveAsync(EmailSettingsUpdate update, CancellationToken ct = default)
    {
        var errors = Validate(update);
        if (errors.Count > 0)
            return errors;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.EmailSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new EmailSettings();
            db.EmailSettings.Add(s);
        }

        s.Host = NullIfBlank(update.Host);
        s.Port = update.Port;
        s.Security = update.Security;
        s.UserName = NullIfBlank(update.UserName);
        s.From = NullIfBlank(update.From);
        s.FromName = NullIfBlank(update.FromName);
        s.PublicUrl = NullIfBlank(update.PublicUrl)?.TrimEnd('/');
        if (update.NewPassword is not null)
            s.PasswordProtected = update.NewPassword.Length > 0 ? _protector.Protect(update.NewPassword) : null;
        s.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        _cached = s;
        return [];
    }

    private static List<string> Validate(EmailSettingsUpdate u)
    {
        var errors = new List<string>();
        var on = !string.IsNullOrWhiteSpace(u.Host);
        if (on && string.IsNullOrWhiteSpace(u.From))
            errors.Add("Enter the address emails are sent from.");
        if (!string.IsNullOrWhiteSpace(u.From) && !new EmailAddressAttribute().IsValid(u.From.Trim()))
            errors.Add("The sender address doesn't look like an email address.");
        if (u.Port is < 1 or > 65535)
            errors.Add("The port must be between 1 and 65535.");
        if (!string.IsNullOrWhiteSpace(u.PublicUrl)
            && !(Uri.TryCreate(u.PublicUrl.Trim(), UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)))
            errors.Add("The app's address must be a full web address, like https://trovami.example.com.");
        return errors;
    }

    private async Task<EmailSettings> LoadAsync(CancellationToken ct)
    {
        if (_cached is not null)
            return _cached;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return _cached = await db.EmailSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct) ?? new EmailSettings();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
