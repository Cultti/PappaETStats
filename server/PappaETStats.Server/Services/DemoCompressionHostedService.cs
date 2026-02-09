using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using PappaETStats.Server.Data;

namespace PappaETStats.Server.Services;

public sealed class DemoCompressionHostedService(
    DemoCompressionQueue queue,
    IDbContextFactory<StatsDbContext> dbFactory,
    ILogger<DemoCompressionHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                var sourcePath = item.SourcePath;
                var zipPath = item.ZipPath;

                if (!File.Exists(sourcePath))
                {
                    logger.LogWarning("Demo source file missing: {SourcePath}", sourcePath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? ".");

                var tmpZip = zipPath + ".tmp";
                if (File.Exists(tmpZip))
                {
                    File.Delete(tmpZip);
                }

                using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(sourcePath, Path.GetFileName(sourcePath), CompressionLevel.Optimal);
                }

                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }

                File.Move(tmpZip, zipPath);

                if (!item.KeepOriginal)
                {
                    File.Delete(sourcePath);
                }

                logger.LogInformation("Compressed demo to {ZipPath}", zipPath);

                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var match = await db.Matches
                    .Where(m => m.ExternalMatchId == item.ExternalMatchId)
                    .FirstOrDefaultAsync(stoppingToken);

                if (match is not null)
                {
                    match.DemoZippedAtUtc = DateTime.UtcNow;
                    match.DemoZipFileName = Path.GetFileName(item.ZipPath);
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to compress demo {SourcePath}", item.SourcePath);
            }
        }
    }
}
