using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using VbForum.Core.Security;
using VbForum.Data;
using VbForum.Web.Components;
using VbForum.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var dataPath = builder.Configuration["VbForum:DataPath"] is { Length: > 0 } dp
    ? Path.GetFullPath(dp, builder.Environment.ContentRootPath)
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataPath);
var paths = new ForumPaths(dataPath, builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new DbSettingsStore(dataPath));
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Schema>();
builder.Services.AddSingleton<BoardCache>();
builder.Services.AddSingleton<Counters>();
builder.Services.AddSingleton<ThreadRepository>();
builder.Services.AddSingleton<PostRepository>();
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<AttachmentRepository>();
builder.Services.AddSingleton<PmRepository>();
builder.Services.AddSingleton<SubscriptionRepository>();
builder.Services.AddSingleton<TokenRepository>();
builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<PostingService>();
builder.Services.AddSingleton<ModerationService>();
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<ForumAdminService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AdminStats>();
builder.Services.AddSingleton<UrlSigner>();
builder.Services.AddSingleton<PostRenderer>();
builder.Services.AddSingleton<ViewCounter>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<MaintenanceJobs>();
builder.Services.AddSingleton<SetupToken>();
builder.Services.AddHostedService<ForumBackgroundService>();
builder.Services.AddScoped<CurrentUser>();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")))
    .SetApplicationName("VbForum");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = ".vbf.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/denied";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = AuthSetup.ValidatePrincipal;
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Admin", p => p.AddRequirements(new ForumRoleRequirement(ForumRole.Admin)));
    o.AddPolicy("Moderator", p => p.AddRequirements(new ForumRoleRequirement(ForumRole.Moderator)));
});
builder.Services.AddScoped<IAuthorizationHandler, ForumRoleHandler>();
builder.Services.AddCascadingAuthenticationState();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Hinter einem Reverse-Proxy (nginx/IIS) echte Client-IP und https übernehmen
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 64 * 1024 * 1024);
builder.Services.AddServerSideBlazor().AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024);

var app = builder.Build();

// Eigene Tabellen anlegen, sobald die DB eingerichtet ist
var db = app.Services.GetRequiredService<Db>();
if (db.IsConfigured)
{
    try { await app.Services.GetRequiredService<Schema>().EnsureOwnTablesAsync(); }
    catch (Exception ex) { app.Logger.LogError(ex, "Datenbank nicht erreichbar – bitte unter /setup bzw. /admin/database prüfen."); }
}
else
{
    var token = VbPassword.RandomToken(12);
    app.Services.GetRequiredService<SetupToken>().Value = token;
    app.Logger.LogWarning("Noch keine Datenbank eingerichtet. Einrichtung unter /setup mit diesem Einrichtungs-Code: {Token}", token);
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    await next();
});

app.MapStaticAssets();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ForumGateMiddleware>();
app.UseAntiforgery();

app.MapForumEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
