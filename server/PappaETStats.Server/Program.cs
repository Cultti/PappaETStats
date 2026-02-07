using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using PappaETStats.Server.Api;
using PappaETStats.Server.Components;
using PappaETStats.Server.Data;
using PappaETStats.Server.Options;

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

var dbOptions = builder.Configuration.GetSection(DbOptions.SectionName).Get<DbOptions>() ?? new DbOptions();
var provider = (dbOptions.Provider ?? "sqlite").Trim().ToLowerInvariant();
var connectionString = dbOptions.ConnectionString;

switch (provider)
{
    case "sqlite":
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = "Data Source=App_Data/pappastats.db";
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
    await db.Database.EnsureCreatedAsync();
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

app.UseAntiforgery();

app.MapIngestEndpoints();
app.MapAdminEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapStaticAssets();
}
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
