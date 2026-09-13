using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PappaETStats.Server.Data;
using PappaETStats.Server.Options;

namespace PappaETStats.Server.Api;

public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The trusted game server supplies current teams and enforces warmup/referee checks.
        endpoints.MapPost("/api/voice/move", MoveAsync)
            .WithTags("Voice")
            .WithName("MoveVoice")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);
        return endpoints;
    }

    public sealed record VoicePlayer(string Guid, string Team);
    public sealed record VoiceRequest(IReadOnlyList<VoicePlayer>? Players);
    public sealed record VoiceResult(string Guid, string Team, string Channel, bool Moved, string Message);
    private sealed record BotResponse(IReadOnlyList<BotResult>? Results);
    private sealed record BotResult(
        [property: JsonPropertyName("user_id")] string UserId, string Team, bool Moved, string? Error);

    private static async Task<IResult> MoveAsync(
        HttpRequest request,
        IDbContextFactory<StatsDbContext> dbFactory,
        IOptions<IngestOptions> ingestOptions,
        IOptions<WebhookOptions> webhookOptions,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        VoiceRequest body,
        CancellationToken cancellationToken)
    {
        // Voice movement must never be exposed anonymously, even when ingest auth is disabled.
        var token = ingestOptions.Value.Token;
        var auth = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(token) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(auth[7..].Trim(), token, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        if (body.Players is not { Count: > 0 and <= 64 })
        {
            return Results.BadRequest(new { error = "players must contain between 1 and 64 players" });
        }

        var players = new List<VoicePlayer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in body.Players)
        {
            if (player is null || !Guid.TryParseExact(player.Guid?.Trim(), "N", out var guid)
                || player.Team is not ("axis" or "allies") || !seen.Add(guid.ToString("N")))
            {
                return Results.BadRequest(new { error = "Each player must have a unique 32-hex GUID and team axis or allies" });
            }
            players.Add(new VoicePlayer(guid.ToString("N").ToUpperInvariant(), player.Team));
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var guids = players.Select(p => p.Guid).ToList();
        var linked = await db.Players.AsNoTracking()
            .Where(p => guids.Contains(p.Guid.ToUpper()) && p.DiscordId != null && p.DiscordId != "")
            .ToDictionaryAsync(p => p.Guid, p => p.DiscordId!, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var options = webhookOptions.Value;
        IReadOnlyList<BotResult> botResults = [];
        string? failure = null;
        if (linked.Count > 0)
        {
            if (!Uri.TryCreate(options.Url?.Trim(), UriKind.Absolute, out var webhookUri)
                || (webhookUri.Scheme != "http" && webhookUri.Scheme != "https"))
            {
                failure = "Voice bot is not configured.";
            }
            else
            {
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(webhookUri, "/webhook/move-teams"));
                    message.Content = JsonContent.Create(new
                    {
                        axis = players.Where(p => p.Team == "axis" && linked.ContainsKey(p.Guid)).Select(p => linked[p.Guid]).ToArray(),
                        allies = players.Where(p => p.Team == "allies" && linked.ContainsKey(p.Guid)).Select(p => linked[p.Guid]).ToArray()
                    });
                    if (!string.IsNullOrWhiteSpace(options.Token))
                    {
                        message.Headers.Add("X-Webhook-Secret", options.Token.Trim());
                    }

                    using var response = await httpClientFactory.CreateClient("Webhook").SendAsync(message, cancellationToken);
                    // The bot uses HTTP 207 when only some players could be moved.
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<BotResponse>(cancellationToken);
                        botResults = result?.Results ?? [];
                    }
                    else
                    {
                        failure = $"Voice bot request failed (HTTP {(int)response.StatusCode}).";
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
                {
                    loggerFactory.CreateLogger("MoveVoice").LogWarning(ex, "Voice bot request failed");
                    failure = "Voice bot did not confirm the move. Please check Discord.";
                }
            }
        }

        var results = players.Select(player =>
        {
            var channel = player.Team == "axis" ? options.AxisVoiceChannelName : options.AlliesVoiceChannelName;
            if (!linked.TryGetValue(player.Guid, out var discordId))
            {
                return new VoiceResult(player.Guid, player.Team, channel, false,
                    $"Not registered. Join {channel}. Register at https://et.aukko.net to use !voice.");
            }

            var result = botResults.FirstOrDefault(r => r is not null && r.UserId == discordId && r.Team == player.Team);
            var moved = failure is null && result?.Moved == true;
            var detail = failure ?? result?.Error ?? "Voice bot did not confirm the move. Please check Discord.";
            return new VoiceResult(player.Guid, player.Team, channel, moved,
                moved ? $"Moved to {channel}." : $"Could not move to {channel}: {detail}");
        }).ToList();

        return Results.Ok(new { results });
    }
}
