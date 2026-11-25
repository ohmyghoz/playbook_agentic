using System.Diagnostics;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;
using Surveillance.Web.Lab;
using Surveillance.Web.Models;

namespace Surveillance.Web.Controllers;

/// <summary>
/// Demo pages for the book (Part IV). Available only while the scripted fake agent is active;
/// see <see cref="LabAvailableFilter"/>.
/// </summary>
[Route("lab")]
[TypeFilter(typeof(LabAvailableFilter))]
public class LabController(InvestigationService investigations, IConfiguration configuration) : Controller
{
    private int SyncTimeoutSeconds => configuration.GetValue("Lab:SyncTimeoutSeconds", 20);

    /// <summary>Request timeout policy of the "before" example; seconds come from Lab:SyncTimeoutSeconds (default 20).</summary>
    public const string SyncTimeoutPolicy = "lab-sync";

    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpGet("sync")]
    public IActionResult Sync() => View(new LabSyncViewModel { TimeoutSeconds = SyncTimeoutSeconds });

    /// <summary>
    /// The classic request-response way, kept as a counter-example: the browser waits on one POST
    /// until the agent pauses or ends, with no progress in between. The slow scenario runs past
    /// the request timeout and the user gets a 504, while the run itself keeps going on the server.
    /// </summary>
    [HttpPost("sync")]
    [ValidateAntiForgeryToken]
    [RequestTimeout(SyncTimeoutPolicy)]
    public async Task<IActionResult> Sync([FromForm] bool slow, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var caseId = await investigations.StartAsync(LabInput("BBCA", slow ? "scenario=slow" : null), ct);

        InvestigationCase @case;
        while (true)
        {
            @case = (await investigations.GetCaseAsync(caseId, ct))!;
            if (@case.Status is CaseStatus.AwaitingApproval || @case.Status.IsTerminal())
            {
                break;
            }

            await Task.Delay(250, ct); // cancelled by the request timeout
        }

        var events = await investigations.GetEventsAsync(caseId, 0, ct);
        return View(new LabSyncViewModel
        {
            Case = @case,
            Elapsed = watch.Elapsed,
            TimeoutSeconds = SyncTimeoutSeconds,
            Events = events.Select(EventMapping.ToEvent).ToList(),
        });
    }

    /// <summary>SSE and polling side by side for the same case, with request counts and delays.</summary>
    [HttpGet("transport")]
    public async Task<IActionResult> Transport(Guid? caseId, CancellationToken ct)
    {
        var cases = await investigations.ListCasesAsync(ct);
        return View(new LabTransportViewModel { Cases = cases.Take(20).ToList(), CaseId = caseId });
    }

    [HttpPost("transport/start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartForTransport(CancellationToken ct)
    {
        var caseId = await investigations.StartAsync(LabInput("TLKM", null), ct);
        return RedirectToAction(nameof(Transport), new { caseId });
    }

    /// <summary>One section per UI pattern; each plays a fake scenario through that pattern only.</summary>
    [HttpGet("patterns")]
    public IActionResult Patterns() => View();

    /// <summary>Scripted scenarios the pattern page may start, chosen through the analyst notes.</summary>
    private static readonly HashSet<string> PatternScenarios = ["happy", "silent_30s", "slow", "model_error", "step_limit"];

    /// <summary>Starts a fake run for one pattern section and returns the URLs its UI root needs.</summary>
    [HttpPost("patterns/start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartPattern([FromForm] string scenario, CancellationToken ct)
    {
        if (!PatternScenarios.Contains(scenario))
        {
            return BadRequest(new { message = "Skenario tidak dikenal." });
        }

        var caseId = await investigations.StartAsync(LabInput("BBRI", scenario == "happy" ? null : $"scenario={scenario}"), ct);
        return Ok(new
        {
            caseId,
            eventsUrl = Url.Action("Events", "Investigation", new { id = caseId }),
            eventsJsonUrl = Url.Action("EventsJson", "Investigation", new { id = caseId }),
            approvalUrl = Url.Action("Approval", "Investigation", new { id = caseId }),
            stopUrl = Url.Action("Stop", "Investigation", new { id = caseId }),
            retryUrl = Url.Action("Retry", "Investigation", new { id = caseId }),
            previewUrlTemplate = Url.Action("ToolPreview", "Investigation", new { id = caseId, toolCallId = "__call__" }),
            detailsUrl = Url.Action("Details", "Investigation", new { id = caseId }),
        });
    }

    /// <summary>Lab cases expire after this many minutes, so the expiry can be watched (Approval:ExpiryHours elsewhere).</summary>
    public const int LabApprovalTimeoutMinutes = 2;

    /// <summary>Two linked panels for one case: the agent waiting on the left, the analyst deciding on the right.</summary>
    [HttpGet("approval")]
    public async Task<IActionResult> Approval(Guid? caseId, CancellationToken ct)
    {
        var @case = caseId is { } id ? await investigations.GetCaseAsync(id, ct) : null;
        return View(@case);
    }

    [HttpPost("approval/start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartForApproval(CancellationToken ct)
    {
        var caseId = await investigations.StartAsync(LabInput("ASII", null) with { ApprovalTimeoutMinutes = LabApprovalTimeoutMinutes }, ct);
        return RedirectToAction(nameof(Approval), new { caseId });
    }

    private static StartInvestigation LabInput(string symbol, string? notes) =>
        new(symbol, new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x rata-rata 3 bulan (demo Lab)",
            2.2, 235_635_300, 210_424_313, 1.12, notes);
}
