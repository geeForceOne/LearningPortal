using LearningPortal.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Web.Services;

public sealed record UserRow(string Id, string UserName, bool IsAdmin, DateTime CreatedAt);

// Admin-only account management. There's no public sign-up: accounts exist only when the admin
// creates them here. Each call uses its own scope so Identity's DbContext never goes stale over
// a long-lived Blazor circuit.
public sealed class UserAdminService(IServiceScopeFactory scopes)
{
    public async Task<IReadOnlyList<UserRow>> ListAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var all = await users.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync();
        return all.Select(u => new UserRow(u.Id, u.UserName ?? "", admins.Contains(u.Id), u.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<string>> CreateAsync(string userName, string password, bool isAdmin)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName.Trim() };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            return result.Errors.Select(e => e.Description).ToList();
        if (isAdmin)
            await users.AddToRoleAsync(user, Roles.Admin);
        return [];
    }

    public async Task<IReadOnlyList<string>> ResetPasswordAsync(string userId, string newPassword)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return ["That account no longer exists."];
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, newPassword);
        return result.Succeeded ? [] : result.Errors.Select(e => e.Description).ToList();
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
