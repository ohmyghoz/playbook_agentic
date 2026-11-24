using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Surveillance.Web.Auth;

namespace Surveillance.Web.Tests.Support;

/// <summary>
/// Signs every request in as a demo account without cookies: the X-Test-User header names the
/// account (default analis1), and "anonymous" leaves the request signed out.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DemoUserDirectory users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";
    public const string Header = "X-Test-User";
    public const string DefaultUser = "analis1";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userName = Request.Headers[Header].FirstOrDefault() ?? DefaultUser;
        if (userName == "anonymous")
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(users.Find(userName) is { } user
            ? AuthenticateResult.Success(new AuthenticationTicket(DemoUserDirectory.ToPrincipal(user, Scheme), Scheme))
            : AuthenticateResult.Fail($"Unknown test user {userName}."));
    }
}
