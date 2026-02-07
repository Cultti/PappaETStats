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

    // EnsureCreated() won't add new tables to an existing database.
    // Best-effort: create the new class-stats table if it's missing.
    if (db.Database.IsRelational())
    {
        var providerName = db.Database.ProviderName ?? string.Empty;

        static async Task<bool> TableExistsAsync(DbContext db, string tableName, string providerName)
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                await using var cmd = db.Database.GetDbConnection().CreateCommand();
                if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name;";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "$name";
                    p.Value = tableName;
                    cmd.Parameters.Add(p);
                }
                else
                {
                    // MySQL/MariaDB
                    cmd.CommandText = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @name;";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@name";
                    p.Value = tableName;
                    cmd.Parameters.Add(p);
                }

                var result = await cmd.ExecuteScalarAsync();
                return result is not null && result is not DBNull;
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        if (!await TableExistsAsync(db, "MatchPlayerClassStats", providerName))
        {
            if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS MatchPlayerClassStats (
    Id TEXT NOT NULL PRIMARY KEY,
    MatchPlayerId TEXT NOT NULL,
    ClassId INTEGER NOT NULL,
    Ms INTEGER NOT NULL,
    FOREIGN KEY(MatchPlayerId) REFERENCES MatchPlayers(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_MatchPlayerClassStats_MatchPlayerId ON MatchPlayerClassStats(MatchPlayerId);
");
            }
            else
            {
                // MySQL/MariaDB
                await db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS MatchPlayerClassStats (
    Id CHAR(36) NOT NULL,
    MatchPlayerId CHAR(36) NOT NULL,
    ClassId INT NOT NULL,
    Ms BIGINT NOT NULL,
    PRIMARY KEY (Id),
    INDEX IX_MatchPlayerClassStats_MatchPlayerId (MatchPlayerId),
    CONSTRAINT FK_MatchPlayerClassStats_MatchPlayers_MatchPlayerId FOREIGN KEY (MatchPlayerId) REFERENCES MatchPlayers (Id) ON DELETE CASCADE
);
");
            }
        }
    }
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
