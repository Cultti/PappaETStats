using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using PappaETStats.Server.Api;
using PappaETStats.Server.Components;
using PappaETStats.Server.Data;
using PappaETStats.Server.Options;
using PappaETStats.Server.Services;

var baseDirectory = AppContext.BaseDirectory;
var looksLikePublishedOutput =
    Directory.Exists(Path.Combine(baseDirectory, "wwwroot")) ||
    File.Exists(Path.Combine(baseDirectory, "PappaETStats.Server.staticwebassets.endpoints.json"));

var builder = looksLikePublishedOutput
    ? WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = baseDirectory,
        WebRootPath = "wwwroot",
    })
    : WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Reverse proxy support (nginx sets X-Forwarded-For/X-Forwarded-Proto/X-Forwarded-Host).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
builder.Services.Configure<DemoStorageOptions>(builder.Configuration.GetSection(DemoStorageOptions.SectionName));
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection(WebhookOptions.SectionName));
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));

// Discord OAuth2 login (cookie session). Login is disabled unless ClientId/ClientSecret are configured.
var discordOptions = builder.Configuration.GetSection(DiscordOptions.SectionName).Get<DiscordOptions>() ?? new DiscordOptions();
var discordLoginEnabled =
    !string.IsNullOrWhiteSpace(discordOptions.ClientId) &&
    !string.IsNullOrWhiteSpace(discordOptions.ClientSecret);

var authBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/login/discord";
        options.LogoutPath = "/auth/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });

if (discordLoginEnabled)
{
    authBuilder.AddOAuth("Discord", options =>
    {
        options.ClientId = discordOptions.ClientId!;
        options.ClientSecret = discordOptions.ClientSecret!;
        options.CallbackPath = "/auth/callback/discord";

        options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
        options.TokenEndpoint = "https://discord.com/api/oauth2/token";
        options.UserInformationEndpoint = "https://discord.com/api/users/@me";

        options.Scope.Add("identify");
        options.SaveTokens = false;

        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "username");
        options.ClaimActions.MapJsonKey("urn:discord:global_name", "global_name");

        options.Events.OnCreatingTicket = async context =>
        {
            using var userRequest = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
            userRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);

            using var response = await context.Backchannel.SendAsync(userRequest, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();

            using var user = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
            context.RunClaimActions(user.RootElement);
        };
    });
}

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<RegistrationTokenService>();

builder.Services.AddHttpClient("Webhook", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<DemoCompressionQueue>();
builder.Services.AddHostedService<DemoCompressionHostedService>();

var dbOptions = builder.Configuration.GetSection(DbOptions.SectionName).Get<DbOptions>() ?? new DbOptions();
var provider = (dbOptions.Provider ?? "sqlite").Trim().ToLowerInvariant();
var connectionString = dbOptions.ConnectionString;

switch (provider)
{
    case "sqlite":
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = "Data Source=App_Data/pappastats.db;Foreign Keys=True";
        }
        else if (!connectionString.Contains("Foreign Keys=", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = connectionString.TrimEnd().TrimEnd(';') + ";Foreign Keys=True";
        }

        builder.Services.AddDbContextFactory<StatsDbContext>(options => options.UseSqlite(connectionString));
        break;
    }
    case "mariadb":
    case "mysql":
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{DbOptions.SectionName}:ConnectionString is required when Provider is '{provider}'.");
        }

        // Targeting Debian MariaDB 11.8.x. If you upgrade, bump this version.
        // Using an explicit version avoids a startup DB connection (ServerVersion.AutoDetect).
        var serverVersion = new MariaDbServerVersion(new Version(11, 8, 3));

        builder.Services.AddDbContextFactory<StatsDbContext>(options =>
            options.UseMySql(connectionString, serverVersion, mySql => mySql.EnableRetryOnFailure()));
        break;
    }
    default:
        throw new InvalidOperationException(
            $"Unsupported DB provider '{dbOptions.Provider}'. Supported providers: sqlite, mariadb, mysql.");
}

var app = builder.Build();

if (provider == "sqlite")
{
    Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "App_Data"));
}

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StatsDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapIngestEndpoints();
app.MapAdminEndpoints();
app.MapDemoEndpoints();
app.MapTeamBalanceEndpoints();
app.MapRegistrationEndpoints();
if (discordLoginEnabled)
{
    app.MapAuthEndpoints();
}

if (app.Environment.IsDevelopment())
{
    app.MapStaticAssets();
}
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
