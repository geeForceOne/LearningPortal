using System.Collections.Concurrent;
using System.Text;
using LearningPortal.Core.Email;
using LearningPortal.Core.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LearningPortal.Web.Services;

public enum PasswordLinkKind { Reset, Invite }

// What happened to a link the admin asked for: emailed, or handed back to pass on by hand
// (Reason says why it wasn't emailed).
public sealed record PasswordLinkResult(bool Emailed, string? SentTo, string? Link, string? Reason);

// Invite links outlive reset links (people don't open an invite the minute it arrives), so they
// get their own token provider with a longer lifespan. Its protector purpose differs from the
// default provider's, so an invite token can never be used as a reset token or the other way round.
public sealed class InviteTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public InviteTokenProviderOptions()
    {
        Name = AccountMailService.InviteProvider;
        TokenLifespan = AccountMailService.InviteLifespan;
    }
}

public sealed class InviteTokenProvider(
    IDataProtectionProvider dataProtection,
    IOptions<InviteTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<AppUser>> logger)
    : DataProtectorTokenProvider<AppUser>(dataProtection, options, logger);

// Invite and password-reset links, and the emails that carry them. Both links end on the same
// set-password page. Setting a password changes the account's security stamp, which voids every
// outstanding link, so each one works once.
public sealed class AccountMailService(
    IServiceScopeFactory scopes, EmailSender sender, EmailSettingsService emailSettings, ILogger<AccountMailService> logger)
{
    public const string InviteProvider = "Invite";
    private const string InvitePurpose = "Invite";
    public static readonly TimeSpan InviteLifespan = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResetLifespan = TimeSpan.FromHours(3);

    // The forgot-password page sends at most one email per account in this window, so it can't
    // be used to flood someone's inbox.
    private static readonly TimeSpan ResetCooldown = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, DateTime> _lastResetSent = new();

    public Task<bool> EmailConfiguredAsync() => emailSettings.IsConfiguredAsync();

    // For the admin: an invite (account without a password yet) or a reset link, emailed when
    // possible and otherwise returned for the admin to pass on.
    public async Task<PasswordLinkResult> SendLinkAsync(string userId, string requestBaseUrl, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId)
            ?? throw new InvalidOperationException("That account no longer exists.");

        var kind = user.PasswordHash is null ? PasswordLinkKind.Invite : PasswordLinkKind.Reset;
        var link = await CreateLinkAsync(users, user, kind, requestBaseUrl);

        if (!await emailSettings.IsConfiguredAsync(ct))
            return new(false, null, link, "Email isn't set up.");
        if (string.IsNullOrEmpty(user.Email))
            return new(false, null, link, $"{user.UserName} has no email address.");

        try
        {
            await sender.SendAsync(user.Email, user.UserName, Compose(kind, user, link), null, ct);
            return new(true, user.Email, null, null);
        }
        catch (EmailException ex)
        {
            logger.LogWarning(ex, "Sending the {Kind} email for user {UserId} failed", kind, user.Id);
            return new(false, null, link, $"The email couldn't be sent. {ex.Message}");
        }
    }

    // For the forgot-password page. Never says whether the account exists: the email goes out in
    // the background, so the response looks and takes the same either way.
    public async Task RequestResetAsync(string userNameOrEmail, string requestBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(userNameOrEmail) || !await emailSettings.IsConfiguredAsync())
            return;

        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await FindForResetAsync(users, userNameOrEmail.Trim());
        if (user?.Email is null || user.PasswordHash is null)
            return;

        var now = DateTime.UtcNow;
        if (_lastResetSent.TryGetValue(user.Id, out var last) && now - last < ResetCooldown)
            return;
        _lastResetSent[user.Id] = now;

        var link = await CreateLinkAsync(users, user, PasswordLinkKind.Reset, requestBaseUrl);
        var message = Compose(PasswordLinkKind.Reset, user, link);
        var (to, name, id) = (user.Email, user.UserName, user.Id);
        _ = Task.Run(async () =>
        {
            try
            {
                await sender.SendAsync(to, name, message, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sending the password reset email for user {UserId} failed", id);
            }
        });
    }

    public async Task SendTestAsync(string userId, IProgress<string>? progress, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId)
            ?? throw new InvalidOperationException("That account no longer exists.");
        if (string.IsNullOrEmpty(user.Email))
            throw new InvalidOperationException("Your account has no email address. Add one first.");
        await sender.SendAsync(user.Email, user.UserName, AccountEmails.Test(user.UserName ?? ""), progress, ct);
    }

    // Null when the link is valid; otherwise why it can't be used.
    public async Task<string?> CheckLinkAsync(string? userId, string? token, PasswordLinkKind kind)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await FindByLinkAsync(users, userId, token, kind);
        return user is null ? LinkInvalidMessage(kind) : null;
    }

    public async Task<(AppUser? User, IReadOnlyList<string> Errors)> SetPasswordAsync(
        string? userId, string? token, PasswordLinkKind kind, string password)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await FindByLinkAsync(users, userId, token, kind);
        if (user is null)
            return (null, [LinkInvalidMessage(kind)]);

        // Both kinds end in a normal reset, which also rotates the security stamp.
        var resetToken = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, resetToken, password);
        if (!result.Succeeded)
            return (null, result.Errors.Select(e => e.Description).ToList());

        // Whoever can open the link can read the mailbox, so a lockout from earlier failed
        // sign-ins shouldn't stand in their way.
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        _lastResetSent.TryRemove(user.Id, out _);
        return (user, []);
    }

    // The admin-set public address when there is one; otherwise the address of the current request,
    // which is wrong behind a reverse proxy that doesn't forward the original host and scheme.
    private async Task<string> BaseUrlAsync(string requestBaseUrl)
    {
        var publicUrl = (await emailSettings.GetAsync()).PublicUrl;
        return (string.IsNullOrWhiteSpace(publicUrl) ? requestBaseUrl : publicUrl).TrimEnd('/');
    }

    private async Task<string> CreateLinkAsync(UserManager<AppUser> users, AppUser user, PasswordLinkKind kind, string requestBaseUrl)
    {
        var token = kind == PasswordLinkKind.Invite
            ? await users.GenerateUserTokenAsync(user, InviteProvider, InvitePurpose)
            : await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var kindParam = kind == PasswordLinkKind.Invite ? "invite" : "reset";
        return $"{await BaseUrlAsync(requestBaseUrl)}/account/set-password?user={Uri.EscapeDataString(user.Id)}&code={code}&kind={kindParam}";
    }

    private static async Task<AppUser?> FindByLinkAsync(UserManager<AppUser> users, string? userId, string? code, PasswordLinkKind kind)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(code))
            return null;
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return null;

        string token;
        try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return null; }

        var valid = kind == PasswordLinkKind.Invite
            ? await users.VerifyUserTokenAsync(user, InviteProvider, InvitePurpose, token)
            : await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider,
                UserManager<AppUser>.ResetPasswordTokenPurpose, token);
        return valid ? user : null;
    }

    private static async Task<AppUser?> FindForResetAsync(UserManager<AppUser> users, string input)
    {
        var byName = await users.FindByNameAsync(input);
        if (byName is not null)
            return byName;

        // Emails are unique by the app's own checks, not a database constraint, so a lookup that
        // somehow matches several accounts sends nothing rather than picking one.
        var normalized = users.NormalizeEmail(input);
        var matches = await users.Users.Where(u => u.NormalizedEmail == normalized).Take(2).ToListAsync();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static EmailMessage Compose(PasswordLinkKind kind, AppUser user, string link) => kind == PasswordLinkKind.Invite
        ? AccountEmails.Invite(user.UserName ?? "", link, InviteLifespan)
        : AccountEmails.PasswordReset(user.UserName ?? "", link, ResetLifespan);

    private static string LinkInvalidMessage(PasswordLinkKind kind) => kind == PasswordLinkKind.Invite
        ? "This invite link has expired or was already used. Ask your administrator for a new one."
        : "This reset link has expired or was already used. Request a new one below.";
}
