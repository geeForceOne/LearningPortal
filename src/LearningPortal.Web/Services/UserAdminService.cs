using System.ComponentModel.DataAnnotations;
using LearningPortal.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Web.Services;

// HasPassword is false while an invite is pending: the account exists but nobody can sign in yet.
public sealed record UserRow(string Id, string UserName, string? Email, bool IsAdmin, bool HasPassword, DateTime CreatedAt);

// Account management. Mostly admin-only (there's no public sign-up: accounts exist only when the
// admin creates them here); FindAsync, SetEmailAsync and ChangePasswordAsync also back each
// user's own Account page. Each call uses its own scope so Identity's DbContext never goes stale over
// a long-lived Blazor circuit.
public sealed class UserAdminService(IServiceScopeFactory scopes)
{
    public async Task<IReadOnlyList<UserRow>> ListAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var all = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        return all.Select(u => new UserRow(u.Id, u.UserName ?? "", u.Email, admins.Contains(u.Id), u.PasswordHash is not null, u.CreatedAt)).ToList();
    }

    public async Task<UserRow?> FindAsync(string userId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var u = await users.FindByIdAsync(userId);
        if (u is null)
            return null;
        return new UserRow(u.Id, u.UserName ?? "", u.Email, await users.IsInRoleAsync(u, Roles.Admin), u.PasswordHash is not null, u.CreatedAt);
    }

    // Rotates the security stamp, so the caller must re-issue the sign-in cookie afterwards
    // (see /account/refresh-signin) or the person is signed out at the next revalidation.
    public async Task<IReadOnlyList<string>> ChangePasswordAsync(string userId, string currentPassword, string newPassword)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return ["That account no longer exists."];
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);
        return result.Succeeded
            ? []
            : result.Errors.Select(e => e.Code == "PasswordMismatch" ? "The current password is wrong." : e.Description).ToList();
    }

    // Creates the account without a password; the person chooses one through the invite link.
    public async Task<(string? UserId, IReadOnlyList<string> Errors)> CreateAsync(string userName, string email, bool isAdmin)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        email = email.Trim();
        if (await CheckEmailAsync(users, email, exceptUserId: null) is { } emailError)
            return (null, [emailError]);

        var user = new AppUser { UserName = userName.Trim(), Email = email };
        var result = await users.CreateAsync(user);
        if (!result.Succeeded)
            return (null, result.Errors.Select(e => e.Description).ToList());
        if (isAdmin)
            await users.AddToRoleAsync(user, Roles.Admin);
        return (user.Id, []);
    }

    public async Task<IReadOnlyList<string>> SetEmailAsync(string userId, string email)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return ["That account no longer exists."];

        email = email.Trim();
        if (await CheckEmailAsync(users, email, userId) is { } emailError)
            return [emailError];

        // Set directly rather than through UserManager.SetEmailAsync, which rotates the security
        // stamp and would sign the person out everywhere and void a pending invite.
        user.Email = email;
        user.EmailConfirmed = false;
        var result = await users.UpdateAsync(user);
        return result.Succeeded ? [] : result.Errors.Select(e => e.Description).ToList();
    }

    public async Task<string?> CheckEmailAsync(string email, string? exceptUserId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await CheckEmailAsync(users, email.Trim(), exceptUserId);
    }

    // Identity's own unique-email rule is off because it would also reject every update to an
    // older account that has no email yet, so uniqueness is checked here instead.
    private static async Task<string?> CheckEmailAsync(UserManager<AppUser> users, string email, string? exceptUserId)
    {
        if (string.IsNullOrEmpty(email))
            return "Enter an email address.";
        if (!new EmailAddressAttribute().IsValid(email))
            return "That doesn't look like an email address.";
        var normalized = users.NormalizeEmail(email);
        return await users.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != exceptUserId)
            ? "Another account already uses that email address."
            : null;
    }

    public async Task SetAdminAsync(string userId, bool isAdmin)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return;
        if (isAdmin) await users.AddToRoleAsync(user, Roles.Admin);
        else await users.RemoveFromRoleAsync(user, Roles.Admin);
    }

    // Deleting a user cascades to all their topics, material, exams and results.
    public async Task DeleteAsync(string userId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is not null)
            await users.DeleteAsync(user);
    }
}
