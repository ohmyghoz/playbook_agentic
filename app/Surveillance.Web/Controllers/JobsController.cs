using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Surveillance.Web.Investigations;
using Surveillance.Web.Models;
using Surveillance.Web.Patterns;

namespace Surveillance.Web.Controllers;

/// <summary>
/// Use case 2: pattern-detection jobs. A job runs in the background (PatternJobRunner, RunPump),
/// so closing the browser does not stop it. Decisions, stop and evidence use the same endpoints
/// as an investigation, because a job is a case like any other.
/// </summary>
[Route("jobs")]
public class JobsController(
    InvestigationService investigations,
    PatternJobRunner runner,
    AgentSelection selection,
    IOptions<PatternDetectionOptions> pattern,
    IOptions<TransportOptions> transport) : Controller
{
    private string UserName => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(new JobListViewModel
    {
        Jobs = await investigations.ListJobsAsync(ct),
        Options = pattern.Value,
        ActiveAgent = await selection.ActiveNameAsync(ct),
        Message = TempData["JobMessage"] as string,
    });

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        var started = await runner.StartAsync(UserName, User.Identity!.Name!, ct);
        if (started.Result == JobStartResult.UnsupportedSolution)
        {
            TempData["JobMessage"] = $"Solusi {started.SolutionName} belum mendukung deteksi pola. Pilih solusi lain di halaman Setting.";
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(Details), new { id = started.CaseId });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var job = await investigations.GetCaseAsync(id, ct);
        return job is not { IsJob: true }
            ? NotFound()
            : View(new JobDetailsViewModel { Job = job, Transport = transport.Value.Resolve(null) });
    }

    /// <summary>The navigation badge: this user's jobs waiting for a decision.</summary>
    [HttpGet("badge")]
    public async Task<IActionResult> Badge(CancellationToken ct) =>
        Ok(new { awaiting = await investigations.CountJobsAwaitingAsync(UserName, ct) });
}
