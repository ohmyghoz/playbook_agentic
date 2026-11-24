using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Surveillance.Web.Auth;

public static class Roles
{
    public const string Analyst = "Analis";
    public const string Supervisor = "Supervisor";
}

public sealed record DemoUser(string UserName, string DisplayName, string Role);

/// <summary>
/// DEMO AUTHENTICATION ONLY. Accounts come from the "DemoUsers" configuration section and sign
/// in without a password, so the example can show who made each approval decision and which
/// pages need which role. A real deployment replaces this with the organisation's identity
/// provider (for example Microsoft Entra ID via OpenID Connect); the claims below stay the same.
/// </summary>
public sealed class DemoUserDirectory(IConfiguration configuration)
{
    public IReadOnlyList<DemoUser> Users { get; } =
        configuration.GetSection("DemoUsers").Get<List<DemoUser>>() ?? [];

    public DemoUser? Find(string? userName) =>
        Users.FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));

    /// <summary>ClaimTypes.Name holds the display name, which approval records store as DecidedBy.</summary>
    public static ClaimsPrincipal ToPrincipal(DemoUser user, string scheme = CookieAuthenticationDefaults.AuthenticationScheme) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.UserName),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
        ], scheme));
}
