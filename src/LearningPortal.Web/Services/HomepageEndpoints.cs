using LearningPortal.Core.Services;
using Microsoft.AspNetCore.Diagnostics;

namespace LearningPortal.Web.Services;

// GET /api/homepage/stats for a gethomepage.dev "customapi" widget: the token an admin created
// under Admin settings goes in an X-API-Key header. Anything else gets a plain 401.
public static class HomepageEndpoints
{
    public static void MapHomepageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/homepage/stats", async (HttpContext http, HomepageStatsService stats, CancellationToken ct) =>
        {
            // A bare 401 for Homepage, not the app's "not found" page that status code pages would render.
            if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
                pages.Enabled = false;
            var result = await stats.GetStatsAsync(http.Request.Headers["X-API-Key"].ToString(), ct);
            return result is null
                ? Results.Unauthorized()
                : Results.Ok(new { accounts = result.Accounts, activeThisWeek = result.ActiveThisWeek, activeNow = result.ActiveNow });
        }).AllowAnonymous().DisableAntiforgery();
    }
}
