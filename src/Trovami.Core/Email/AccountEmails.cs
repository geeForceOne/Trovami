using static Trovami.Core.Email.EmailLayout;

namespace Trovami.Core.Email;

// The account and household emails, as plain text plus a simple HTML version.
public static class AccountEmails
{
    private const string About = "a simple app that remembers where your things are stored";

    // login: what they sign in with, normally their email address. household: set when the account
    // was made by a household invite, which the new user joins with it.
    public static EmailMessage Invite(string shownName, string login, string link, TimeSpan validFor, string? household = null, string? invitedBy = null) => Build(
        $"Your {AppInfo.Name} account",
        household is null
            ? $"Hello {shownName}, an account has been created for you on {AppInfo.Name}, {About}."
            : $"Hello {shownName}, {invitedBy} invited you to the household \"{household}\" on {AppInfo.Name}, {About}. An account has been created for you, and you're already in the household.",
        $"You sign in with your email address, {login}. Open the link below to choose your password and sign in.",
        "Choose your password",
        link,
        $"The link works once and expires in {Words.Describe(validFor)}. If it has expired, ask {(invitedBy ?? "your administrator")} for a new one.");

    // To an existing user, invited into another household.
    public static EmailMessage HouseholdInvite(string shownName, string household, string invitedBy, string link) => Build(
        $"{invitedBy} invited you to \"{household}\"",
        $"Hello {shownName}, {invitedBy} invited you to the household \"{household}\" on {AppInfo.Name}. Once you accept, you see and edit everything stored in it.",
        "Open Trovami to accept or decline the invite.",
        "Open Trovami",
        link,
        $"The invite stays open for {Words.Describe(Models.HouseholdInvite.KeepFor)}.");

    public static EmailMessage PasswordReset(string login, string link, TimeSpan validFor) => Build(
        $"Reset your {AppInfo.Name} password",
        $"Someone asked to reset the password for the {AppInfo.Name} account {login}.",
        "Open the link below to choose a new password.",
        "Reset password",
        link,
        $"The link works once and expires in {Words.Describe(validFor)}. If you didn't ask for this, ignore this email; your password stays the same.");

    // To an admin, after someone they invited has set their password.
    public static EmailMessage InviteAccepted(string newUserName, string newUserLogin, string usersLink) => Build(
        $"{newUserName} joined {AppInfo.Name}",
        $"{newUserName} ({newUserLogin}) accepted the invite and set up their {AppInfo.Name} account.",
        "You can see and manage the account on the Users page.",
        "Open Users",
        usersLink,
        "You get this email because \"Notify me when invited users join\" is on under Admin settings.");

    // To an admin, when someone asks for an account.
    public static EmailMessage AccountRequested(string name, string email, string? message, string usersLink) => Build(
        $"{name} would like a {AppInfo.Name} account",
        $"{name} ({email}) asked for an account on {AppInfo.Name}. " +
            (string.IsNullOrWhiteSpace(message) ? "They didn't add a message." : $"Their message: \"{message}\""),
        "Approve or decline the request on the Users page. Approving sends them the usual invite.",
        "Open Users",
        usersLink,
        "You get this email because \"Notify me about account requests\" is on under Admin settings.");

    // To someone whose account request was declined. note: an optional line from the admin.
    public static EmailMessage AccountRequestDeclined(string name, string? note)
    {
        var intro = $"Hello {name}, thank you for your interest in {AppInfo.Name}. Your request for an account wasn't approved.";
        var extra = string.IsNullOrWhiteSpace(note) ? "" : $"A note from the administrator: {note}";
        var subject = $"Your {AppInfo.Name} account request";
        return new(subject,
            $"{intro}\n\n{(extra.Length > 0 ? extra + "\n" : "")}",
            Html(subject, Paragraph(intro) + (extra.Length > 0 ? Paragraph(extra, marginBottom: 0) : "")));
    }

    public static EmailMessage Test(string shownName)
    {
        var subject = $"{AppInfo.Name} test email";
        var line = $"Email from {AppInfo.Name} works. Invites and password reset links will arrive like this one.";
        return new(subject,
            $"Hello {shownName},\n\n{line}\n",
            Html(subject, Paragraph($"Hello {shownName},") + Paragraph(line, marginBottom: 0)));
    }

    private static EmailMessage Build(string subject, string intro, string action, string button, string link, string footer)
    {
        var text = $"{intro}\n\n{action}\n\n{link}\n\n{footer}\n";
        var body =
            Paragraph(intro) +
            Paragraph(action, marginBottom: 24) +
            Button(button, link) +
            $"<p style=\"margin:0 0 16px;font-size:13px;color:{Pencil}\">{Enc("If the button doesn't work, copy this address into your browser:")}<br>" +
            $"<span style=\"word-break:break-all\">{Enc(link)}</span></p>" +
            Paragraph(footer, muted: true, marginBottom: 0);
        return new EmailMessage(subject, text, Html(subject, body));
    }
}
