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

    private static StartInvestigation LabInput(string symbol, string? notes) =>
        new(symbol, new DateOnly(2026, 11, 24), TriggerKind.Volume, "Volume di atas 1,1x rata-rata 3 bulan (demo Lab)",
            2.2, 235_635_300, 210_424_313, 1.12, notes);
}
