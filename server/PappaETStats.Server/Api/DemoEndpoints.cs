using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.Server.Data;
using PappaETStats.Server.Options;
using PappaETStats.Server.Services;

namespace PappaETStats.Server.Api;

public static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api")
            .WithTags("Demos");

        // Preferred endpoints (one demo per match)
        group.MapPost("/matches/{matchId}/demo", UploadMatchDemoAsync)
            .WithName("UploadMatchDemo")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/matches/{matchId}/demo.zip", DownloadMatchDemoZipAsync)
            .WithName("DownloadMatchDemoZip")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static IResult? ValidateBearerToken(HttpRequest request, IOptions<IngestOptions> ingestOptions)
    {
        var configuredToken = ingestOptions.Value.Token;
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return null;
        }

        var authHeader = request.Headers.Authorization.ToString();
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..].Trim()
            : null;

        return string.Equals(token, configuredToken, StringComparison.Ordinal)
            ? null
            : Results.Unauthorized();
    }

    private static string SanitizePathSegment(string value)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return "na";
        }

        var chars = s
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .ToArray();

        var sanitized = new string(chars);
        while (sanitized.Contains("__", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);
        }

        sanitized = sanitized.Trim('_');
        return sanitized.Length == 0 ? "na" : sanitized;
    }

    private static string EnsureExtension(string fileName, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return fileName;
        }

        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        return Path.HasExtension(fileName) ? fileName : fileName + extension;
    }

    private static async Task<IResult> UploadMatchDemoAsync(
        HttpContext httpContext,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        IOptions<DemoStorageOptions> demoOptions,
        DemoCompressionQueue compressionQueue,
        string matchId,
        CancellationToken cancellationToken)
    {
        var authResult = ValidateBearerToken(httpContext.Request, ingestOptions);
        if (authResult is not null)
        {
            return authResult;
        }

        if (string.IsNullOrWhiteSpace(matchId))
        {
            return Results.BadRequest(new { error = "matchId is required" });
        }

        var requireMatchExists = demoOptions.Value.RequireRoundExists;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var match = await db.Matches
            .Where(m => m.ExternalMatchId == matchId)
            .FirstOrDefaultAsync(cancellationToken);

        if (requireMatchExists && match is null)
        {
            return Results.NotFound(new { error = "match not found (stats not ingested yet)", matchId });
        }

        var maxUpload = demoOptions.Value.MaxUploadBytes;
        if (maxUpload > 0)
        {
            var sizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is not null && !sizeFeature.IsReadOnly)
            {
                sizeFeature.MaxRequestBodySize = maxUpload;
            }
        }

        if (!httpContext.Request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "multipart/form-data is required" });
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null)
        {
            return Results.BadRequest(new { error = "file is required (multipart field 'file')" });
        }

        if (file.Length <= 0)
        {
            return Results.BadRequest(new { error = "file is empty" });
        }

        var requestedDemoFilename = form["demoFilename"].ToString();
        var originalName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(originalName);

        var baseName = !string.IsNullOrWhiteSpace(requestedDemoFilename)
            ? SanitizePathSegment(requestedDemoFilename)
            : SanitizePathSegment(Path.GetFileNameWithoutExtension(originalName));

        baseName = EnsureExtension(baseName, extension);

        var root = demoOptions.Value.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "App_Data/demos";
        }

        var rootFull = Path.IsPathRooted(root)
            ? root
            : Path.Combine(httpContext.RequestServices.GetRequiredService<IHostEnvironment>().ContentRootPath, root);

        var matchSegment = SanitizePathSegment(matchId);
        var targetDir = Path.Combine(rootFull, matchSegment);
        Directory.CreateDirectory(targetDir);

        // Store the raw demo under the requested name; avoid collisions.
        var targetPath = Path.Combine(targetDir, baseName);
        if (File.Exists(targetPath))
        {
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var withoutExt = Path.GetFileNameWithoutExtension(baseName);
            var ext = Path.GetExtension(baseName);
            targetPath = Path.Combine(targetDir, $"{withoutExt}_{stamp}{ext}");
        }

        await using (var stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        // Queue background compression. Zip lives next to the demo.
        var zipPath = Path.ChangeExtension(targetPath, ".zip");
        await compressionQueue.EnqueueAsync(
            new DemoCompressionQueue.WorkItem(matchId, targetPath, zipPath, demoOptions.Value.KeepOriginalAfterZip),
            cancellationToken);

        if (match is not null)
        {
            match.DemoFileName = Path.GetFileName(targetPath);
            match.DemoZipFileName = Path.GetFileName(zipPath);
            match.DemoUploadedAtUtc = DateTime.UtcNow;
            match.DemoZippedAtUtc = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Accepted(value: new
        {
            accepted = true,
            matchId,
            storedFileName = Path.GetFileName(targetPath),
            zipQueued = true
        });
    }

    private static async Task<IResult> DownloadMatchDemoZipAsync(
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<DemoStorageOptions> demoOptions,
        IHostEnvironment env,
        string matchId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(matchId))
        {
            return Results.NotFound();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Matches
            .AsNoTracking()
            .Where(m => m.ExternalMatchId == matchId)
            .Select(m => new { m.DemoZipFileName })
            .FirstOrDefaultAsync(cancellationToken);

        var zipFileName = row?.DemoZipFileName;
        if (string.IsNullOrWhiteSpace(zipFileName))
        {
            return Results.NotFound();
        }

        var root = demoOptions.Value.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "App_Data/demos";
        }

        var rootFull = Path.IsPathRooted(root)
            ? root
            : Path.Combine(env.ContentRootPath, root);

        var matchSegment = SanitizePathSegment(matchId);
        var candidatePath = Path.Combine(rootFull, matchSegment, Path.GetFileName(zipFileName));

        if (!File.Exists(candidatePath))
        {
            return Results.NotFound();
        }

        var downloadName = $"{SanitizePathSegment(matchId)}.zip";
        return Results.File(candidatePath, contentType: "application/zip", fileDownloadName: downloadName);
    }
}
