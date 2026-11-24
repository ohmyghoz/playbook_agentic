using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Surveillance.Web.Auth;
using Surveillance.Web.Investigations;
using Surveillance.Web.Models;

namespace Surveillance.Web.Controllers;

[Route("settings")]
[Authorize(Roles = Roles.Supervisor)]
public class SettingsController(AgentSelection selection) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new SettingsViewModel { Choices = await selection.ListAsync(ct), Saved = TempData["Saved"] as string });

    [HttpPost("agent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Agent([FromForm] string provider, CancellationToken ct)
    {
        if (!await selection.SetActiveAsync(provider, ct))
        {
            return BadRequest();
        }

        TempData["Saved"] = provider;
        return RedirectToAction(nameof(Index));
    }
}
