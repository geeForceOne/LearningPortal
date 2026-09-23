using LearningPortal.Core;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using LearningPortal.Web.Components;
using LearningPortal.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Everything persistent lives under one data directory (a Docker volume in production):
// the SQLite database, uploaded files and the Data Protection key ring. Each path can also be
// overridden on its own through Storage__DatabasePath, Storage__FilesPath, Storage__KeysPath.
var storage = builder.Configuration.GetSection("Storage");
var dataDir = Path.GetFullPath(storage["DataDirectory"] ?? "data", builder.Environment.ContentRootPath);
var dbPath = Path.GetFullPath(storage["DatabasePath"] ?? Path.Combine(dataDir, "learningportal.db"), builder.Environment.ContentRootPath);
var filesPath = Path.GetFullPath(storage["FilesPath"] ?? Path.Combine(dataDir, "files"), builder.Environment.ContentRootPath);
var keysPath = Path.GetFullPath(storage["KeysPath"] ?? Path.Combine(dataDir, "keys"), builder.Environment.ContentRootPath);
foreach (var dir in new[] { Path.GetDirectoryName(dbPath)!, filesPath, keysPath })
    Directory.CreateDirectory(dir);

var portalOptions = builder.Configuration.GetSection("LearningPortal").Get<LearningPortalOptions>() ?? new LearningPortalOptions();
portalOptions.FilesRoot = filesPath;

builder.Services.AddLearningPortalCore($"Data Source={dbPath}", portalOptions);

builder.Services.AddDataProtection()
    .SetApplicationName("LearningPortal")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Identity: username + password now. External logins (Google, Apple, Facebook) plug in later
// as additional authentication handlers on top of the same Identity cookies.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});
builder.Services.AddAuthorization();

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireDigit = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<UserAdminService>();
builder.Services.AddSingleton<AnalysisQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalysisQueue>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync(Roles.Admin))
        await roles.CreateAsync(new IdentityRole(Roles.Admin));
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
// After authentication, so tokens are validated against the signed-in user they were issued for.
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapAccountEndpoints();

app.Run();
