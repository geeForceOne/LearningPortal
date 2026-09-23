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
    }
}
