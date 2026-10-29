using Trovami.Core.Models;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Trovami.Core.Email;

public sealed record EmailMessage(string Subject, string TextBody, string HtmlBody);

// A failure to send, with a message that can be shown to an admin as it is (English; the page
// translates the fixed part).
public sealed class EmailException(string message, Exception? inner = null) : Exception(message, inner);

// Sends through the SMTP server the admin configured on the Admin settings page.
public sealed class EmailSender(EmailSettingsService settings)
{
    public async Task SendAsync(string toAddress, string? toName, EmailMessage email,
        IProgress<string>? progress, CancellationToken ct)
    {
        var options = await settings.GetConnectionAsync(ct)
            ?? throw new EmailException("Email isn't set up on this server.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.From));
        message.To.Add(new MailboxAddress(toName ?? "", toAddress));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        client.Timeout = 30_000;
        try
        {
            progress?.Report($"Connecting to {options.Host}...");
            await client.ConnectAsync(options.Host, options.Port, ToSocketOptions(options.Security), ct);
            if (!string.IsNullOrEmpty(options.UserName))
                await client.AuthenticateAsync(options.UserName, options.Password ?? "", ct);

            progress?.Report("Sending email...");
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AuthenticationException ex)
        {
            throw new EmailException("The mail server rejected the login. Check the username and password on the Admin settings page.", ex);
        }
        catch (SmtpCommandException ex)
        {
            throw new EmailException($"The mail server refused the message: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is SmtpProtocolException or SslHandshakeException or IOException
                                       or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new EmailException($"Couldn't reach the mail server at {options.Host}:{options.Port}: {ex.Message}", ex);
        }
        catch (ServiceNotConnectedException ex)
        {
            throw new EmailException($"Couldn't reach the mail server at {options.Host}:{options.Port}.", ex);
        }
    }

    private static SecureSocketOptions ToSocketOptions(EmailSecurity security) => security switch
    {
        EmailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        EmailSecurity.StartTls => SecureSocketOptions.StartTls,
        EmailSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };
}
