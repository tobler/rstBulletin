using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using VbForum.Data;
using VbForum.Web.Components;
using VbForum.Web.Infrastructure;

// Docker-HEALTHCHECK: "dotnet VbForum.Web.dll --healthcheck" fragt /healthz ab (im aspnet-Image gibt es kein curl)
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] ?? "8080";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var r = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
        return r.IsSuccessStatusCode ? 0 : 1;
    }
    catch { return 1; }
}

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
builder.Services.AddSingleton<UserDeletion>();
builder.Services.AddSingleton<AssetStore>();
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<ForumAdminService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AdminStats>();
builder.Services.AddSingleton<UrlSigner>();
builder.Services.AddSingleton<PostRenderer>();
builder.Services.AddSingleton<ViewCounter>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<MaintenanceJobs>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddSingleton<Installer>();
builder.Services.AddSingleton<LegacyLookup>();
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

// Status beim Start ins Log schreiben; eigene Tabellen bei bestehender Einrichtung nachziehen
var store = app.Services.GetRequiredService<DbSettingsStore>();
var status = await app.Services.GetRequiredService<SetupState>().GetAsync();
switch (status)
{
    case SetupStatus.Ready:
        await app.Services.GetRequiredService<Schema>().EnsureOwnTablesAsync();
        break;
    case SetupStatus.DbUnavailable:
        app.Logger.LogError("Datenbank {Server}:{Port}/{Database} nicht erreichbar ({Source}).", store.Current.Server, store.Current.Port,
            store.Current.Database, store.FromEnv ? "Umgebungsvariablen" : "dbsettings.json");
        break;
    default:
        app.Logger.LogWarning("Forum ist noch nicht eingerichtet – bitte /setup im Browser aufrufen.");
        break;
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

await app.RunAsync();
return 0;
