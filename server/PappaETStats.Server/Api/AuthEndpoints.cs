using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace PappaETStats.Server.Api;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth")
            .WithTags("Auth");

        group.MapGet("/login/discord", (string? returnUrl) =>
        {
            var redirect = SanitizeReturnUrl(returnUrl);
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = redirect },
                ["Discord"]);
        })
            .WithName("DiscordLogin");

        group.MapGet("/logout", async (HttpContext context, string? returnUrl) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect(SanitizeReturnUrl(returnUrl));
        })
            .WithName("Logout");

        group.MapGet("/me", (ClaimsPrincipal user) =>
        {
            if (user.Identity?.IsAuthenticated != true)
            {
                return Results.Ok(new { authenticated = false });
            }

            return Results.Ok(new
            {
                authenticated = true,
                discordId = user.FindFirstValue(ClaimTypes.NameIdentifier),
                username = user.Identity.Name,
            });
        })
            .WithName("CurrentUser");

        return endpoints;
    }

    private static string SanitizeReturnUrl(string? returnUrl)
    {
        // Only allow local redirects to avoid open-redirect vulnerabilities.
        if (!string.IsNullOrEmpty(returnUrl)
            && returnUrl.StartsWith('/')
            && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return returnUrl;
        }

        return "/";
    }
}
