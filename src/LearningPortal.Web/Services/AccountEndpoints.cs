using LearningPortal.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LearningPortal.Web.Services;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A form post (with antiforgery) rather than a link, so a stray GET can't sign anyone out.
        endpoints.MapPost("/account/logout", async (SignInManager<AppUser> signInManager, [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/account/login");
        });

        // Changing the password on the (interactive) Account page rotates the security stamp, and an
        // interactive page can't set cookies, so it comes through here to get a fresh sign-in cookie.
        // Harmless as a GET: it only re-issues the caller's own cookie.
        endpoints.MapGet("/account/refresh-signin", async (HttpContext http, SignInManager<AppUser> signInManager,
            UserManager<AppUser> userManager, [FromQuery] string? returnUrl) =>
        {
            var user = await userManager.GetUserAsync(http.User);
            if (user is null)
                return Results.LocalRedirect("~/account/login");
            await signInManager.RefreshSignInAsync(user);
            return Results.LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "~/account");
        }).RequireAuthorization();

        // The old separate pages, now sections of the Account page.
        endpoints.MapGet("/account/password", () => Results.LocalRedirect("~/account"));
        endpoints.MapGet("/account/email", () => Results.LocalRedirect("~/account"));
    }

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
