using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Surveillance.Web.Auth;

namespace Surveillance.Web.Controllers;

/// <summary>Demo sign-in: pick an account, no password. See <see cref="DemoUserDirectory"/>.</summary>
[Route("account")]
public class AccountController(DemoUserDirectory users) : Controller
{
    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View(users.Users);
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login([FromForm] string userName, [FromForm] string? returnUrl)
    {
        if (users.Find(userName) is not { } user)
        {
            return BadRequest();
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, DemoUserDirectory.ToPrincipal(user));
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Investigation");
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    [HttpGet("denied")]
    public IActionResult Denied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("~/Views/Shared/Error.cshtml", Models.ErrorViewModel.ForStatus(StatusCodes.Status403Forbidden, HttpContext));
    }
}
