using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>The application's own cookie sign-in: demo accounts, no password.</summary>
public partial class LoginFlowTests(CookieAuthSurveillanceFactory factory) : IClassFixture<CookieAuthSurveillanceFactory>
{
    [Fact]
    public async Task Pages_need_sign_in_and_settings_need_the_supervisor_role()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var anonymous = await client.GetAsync("/investigations");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/account/login", anonymous.Headers.Location!.PathAndQuery);

        var loginPage = await client.GetStringAsync("/account/login?returnUrl=%2Fsettings");
        Assert.Contains("Ni Kadek Ayu Lestari", loginPage);
        Assert.Contains("Siti Rahmah Siregar", loginPage);
        Assert.Contains("Autentikasi untuk demo", loginPage);

        // Static files stay public so the login page is styled.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/js/investigation.js")).StatusCode);

        var analyst = await SignInAsync(client, loginPage, "analis1", "/settings");
        Assert.Equal("/settings", analyst.Headers.Location!.OriginalString);
        var denied = await client.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/account/denied", denied.Headers.Location!.PathAndQuery);
        var deniedPage = await client.GetAsync(denied.Headers.Location);
        Assert.Equal(HttpStatusCode.Forbidden, deniedPage.StatusCode);
        Assert.Contains("Akses tidak diizinkan", await deniedPage.Content.ReadAsStringAsync());

        var index = await client.GetStringAsync("/investigations");
        Assert.Contains("Ni Kadek Ayu Lestari", index);
        Assert.DoesNotContain("href=\"/settings\"", index);

        await SignInAsync(client, await client.GetStringAsync("/account/login"), "supervisor", null);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings")).StatusCode);
        Assert.Contains("href=\"/settings\"", await client.GetStringAsync("/investigations"));
    }

    [Fact]
    public async Task Unknown_account_and_foreign_return_url_are_refused()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginPage = await client.GetStringAsync("/account/login");

        var unknown = await SignInAsync(client, loginPage, "admin", null);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var foreign = await SignInAsync(client, loginPage, "analis2", "https://contoh.invalid/");
        Assert.Equal("/investigations", foreign.Headers.Location!.OriginalString);
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string loginPage, string userName, string? returnUrl) =>
        client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userName"] = userName,
            ["returnUrl"] = returnUrl ?? "",
            ["__RequestVerificationToken"] = AntiforgeryToken().Match(loginPage).Groups[1].Value,
        }));

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}

public class ErrorPageTests(SurveillanceFactory factory) : IClassFixture<SurveillanceFactory>
{
    [Fact]
    public async Task Unknown_case_shows_the_indonesian_not_found_page()
    {
        var response = await factory.CreateClient().GetAsync($"/investigations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.Contains("Halaman tidak ditemukan", page);
        Assert.Contains("Ke daftar investigasi", page);
    }

    [Fact]
    public async Task Json_error_bodies_are_left_alone()
    {
        var client = factory.CreateClient();
        var page = await client.GetStringAsync("/investigations");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync($"/investigations/{Guid.NewGuid()}/approval",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ApprovalId"] = "approval_x", ["Decision"] = "approve", ["__RequestVerificationToken"] = token,
            }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Kasus tidak ditemukan", await response.Content.ReadAsStringAsync());
    }
}
