using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Investigations;
using Surveillance.Web.Models;

namespace Surveillance.Web.Controllers;

[Route("investigations")]
public class InvestigationController(
    InvestigationService investigations,
    AgentSelection selection,
    IOptions<TransportOptions> transport) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = new InvestigationIndexViewModel
        {
            Cases = await investigations.ListCasesAsync(ct),
            Form = new StartInvestigationForm(),
            ActiveAgent = await selection.ActiveNameAsync(ct),
            LabAvailable = await selection.IsLabAvailableAsync(ct),
        };
        return View(model);
    }

    [HttpPost("start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start([Bind(Prefix = "Form")] StartInvestigationForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            var model = new InvestigationIndexViewModel
            {
                Cases = await investigations.ListCasesAsync(ct), Form = form, ActiveAgent = await selection.ActiveNameAsync(ct),
            };
            return View(nameof(Index), model);
        }

        var caseId = await investigations.StartAsync(form.ToCommand(), ct);
        return RedirectToAction(nameof(Details), new { id = caseId });
    }

    [HttpPost("{id:guid}/retry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var @case = await investigations.GetCaseAsync(id, ct);
        if (@case is null)
        {
            return NotFound();
        }

        var caseId = await investigations.StartAsync(StartInvestigationForm.FromCase(@case).ToCommand(), ct);
        return RedirectToAction(nameof(Details), new { id = caseId });
    }

    /// <param name="transportName">sse, polling or signalr; overrides Transport:Default for this page.</param>
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Details(Guid id, [FromQuery(Name = "transport")] string? transportName, CancellationToken ct) =>
        CaseView(id, replay: false, transportName, ct);

    [HttpGet("{id:guid}/history")]
    public Task<IActionResult> History(Guid id, CancellationToken ct) => CaseView(id, replay: true, null, ct);

    /// <summary>
    /// Server-Sent Events: stored events after the client's last sequence,
    /// then live events until the run ends. EventSource sends Last-Event-ID
    /// on reconnect; <paramref name="after"/> serves clients that cannot.
    /// </summary>
    [HttpGet("{id:guid}/events")]
    public IResult Events(Guid id, [FromQuery] long? after, CancellationToken ct)
    {
        var from = after ?? 0;
        if (long.TryParse(Request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out var lastEventId))
        {
            from = lastEventId;
        }

        // Proxies such as nginx buffer responses unless told otherwise; see docs/PROXY.md.
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers.CacheControl = "no-cache";
        var options = transport.Value;
        var items = ToSse(investigations.StreamEventsAsync(id, from, ct), ct);
        return TypedResults.ServerSentEvents(SseKeepAlive.WithPings(items,
            TimeSpan.FromSeconds(options.SsePingSeconds), TimeSpan.FromMilliseconds(options.SseRetryMilliseconds), ct));
    }

    /// <summary>All stored events as a JSON array, for replay and finished runs.</summary>
    [HttpGet("{id:guid}/events.json")]
    public async Task<IActionResult> EventsJson(Guid id, [FromQuery] long after, CancellationToken ct)
    {
        var records = await investigations.GetEventsAsync(id, after, ct);
        var json = "[" + string.Join(",", records.Select(r => EventMapping.ToEvent(r).ToJson())) + "]";
        return Content(json, "application/json");
    }

    /// <summary>
    /// The raw output behind one tool call. Not part of the SSE stream: the browser asks for it
    /// only when the analyst opens the evidence.
    /// </summary>
    [HttpGet("{id:guid}/tools/{toolCallId}/preview")]
    public async Task<IActionResult> ToolPreview(Guid id, string toolCallId, CancellationToken ct)
    {
        var preview = await investigations.GetPreviewAsync(id, toolCallId, ct);
        return preview is null
            ? NotFound(new { message = "Data mentah untuk panggilan tool ini tidak tersedia." })
            : Ok(new { preview.ToolCallId, preview.ToolName, preview.Preview });
    }

    [HttpPost("{id:guid}/approval")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approval(Guid id, [FromForm] ApprovalForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid || form.Decision is null)
        {
            return BadRequest(new { message = "Pilih keputusan, lalu kirim lagi." });
        }

        // The decision is recorded under the signed-in user, never a name typed into the form.
        var outcome = await investigations.DecideAsync(id, form.ApprovalId, form.Decision.Value, form.Comment,
            User.Identity!.Name!, ct);
        return outcome.Result switch
        {
            DecisionResult.Accepted => Ok(new { message = "Keputusan tersimpan. Agent melanjutkan investigasi." }),
            DecisionResult.CaseNotFound => NotFound(new { message = "Kasus tidak ditemukan." }),
            DecisionResult.CommentRequired => BadRequest(new { message = "Tulis komentar untuk analis agent sebelum meminta revisi atau menolak." }),
            DecisionResult.StaleApproval => Conflict(new { message = "Draf memo ini sudah diganti. Muat ulang halaman untuk melihat draf terbaru." }),
            DecisionResult.AlreadyDecided => Conflict(new { message = $"Kasus ini sudah diputuskan oleh {outcome.DecidedBy ?? "analis lain"}. Muat ulang halaman untuk melihat keputusannya." }),
            _ => Conflict(new { message = "Investigasi ini tidak sedang menunggu keputusan." }),
        };
    }

    [HttpPost("{id:guid}/stop")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        await investigations.StopAsync(id, ct);
        return Accepted(new { message = "Menghentikan investigasi." });
    }

    private async Task<IActionResult> CaseView(Guid id, bool replay, string? transportName, CancellationToken ct)
    {
        var @case = await investigations.GetCaseAsync(id, ct);
        if (@case is null)
        {
            return NotFound();
        }

        return View("Details", new InvestigationDetailsViewModel
        {
            Case = @case,
            Replay = replay,
            Transport = transport.Value.Resolve(transportName),
        });
    }

    private static async IAsyncEnumerable<SseItem<string>> ToSse(IAsyncEnumerable<Data.AgentEventRecord> records,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var record in records.WithCancellation(ct))
        {
            yield return new SseItem<string>(EventMapping.ToEvent(record).ToJson(), record.Type)
            {
                EventId = record.Sequence.ToString(CultureInfo.InvariantCulture),
            };
        }
    }
}
