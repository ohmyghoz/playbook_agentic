using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Surveillance.Web.Models;

namespace Surveillance.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => RedirectToAction("Index", "Investigation");

    /// <summary>
    /// Error page for unhandled exceptions (UseExceptionHandler) and empty error responses
    /// (UseStatusCodePagesWithReExecute). Re-execution keeps the original method, so any verb is accepted.
    /// </summary>
    [AllowAnonymous]
    [Route("error/{statusCode:int?}")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(int? statusCode)
    {
        var code = statusCode ?? StatusCodes.Status500InternalServerError;
        Response.StatusCode = code;
        return View(ErrorViewModel.ForStatus(code, HttpContext));
    }
}
