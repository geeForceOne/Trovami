using Microsoft.AspNetCore.Identity;

namespace Trovami.Web.Services;

// Identity's messages that people can actually run into, in Trovami's own words.
public sealed class IdentityErrors : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) =>
        new() { Code = nameof(PasswordTooShort), Description = $"Use at least {length} characters for the password." };

    public override IdentityError PasswordMismatch() =>
        new() { Code = nameof(PasswordMismatch), Description = "The current password is wrong." };

    public override IdentityError InvalidToken() =>
        new() { Code = nameof(InvalidToken), Description = "This link has expired or was already used." };

    public override IdentityError DuplicateUserName(string userName) =>
        new() { Code = nameof(DuplicateUserName), Description = "Another user already signs in with that email address." };

    public override IdentityError DuplicateEmail(string email) =>
        new() { Code = nameof(DuplicateEmail), Description = "Another user already uses that email address." };

    public override IdentityError InvalidEmail(string? email) =>
        new() { Code = nameof(InvalidEmail), Description = "That doesn't look like an email address." };

    public override IdentityError ConcurrencyFailure() =>
        new() { Code = nameof(ConcurrencyFailure), Description = "Someone else changed this at the same time. Try again." };
}
