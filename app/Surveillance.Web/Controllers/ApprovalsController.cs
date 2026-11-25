using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Surveillance.Web.Auth;
using Surveillance.Web.Investigations;
using Surveillance.Web.Models;

namespace Surveillance.Web.Controllers;

/// <summary>The approval queue: cases waiting for an analyst, oldest first, with claims and deadlines.</summary>
[Route("approvals")]
public class ApprovalsController(
    InvestigationService investigations,
    CaseClaims claims,
    DemoUserDirectory users,
    IOptions<ApprovalOptions> approval) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = (await investigations.ListAwaitingApprovalAsync(ct))
            .Select(c =>
            {
                var deadline = approval.Value.DeadlineFor(c)!.Value;
                return new ApprovalQueueRow(c, now - c.AwaitingSince!.Value, deadline,
                    deadline - now <= approval.Value.WarningFor(c), claims.StateOf(c, now));
            })
            .ToList();
        return View(new ApprovalQueueViewModel
        {
            Rows = rows,
            Analysts = users.Users.Where(u => u.Role == Roles.Analyst).ToList(),
            Assigned = TempData["Assigned"] as string,
        });
    }

    /// <summary>A supervisor hands a waiting case to an analyst, also when someone else holds it.</summary>
    [HttpPost("{id:guid}/assign")]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = Roles.Supervisor)]
    public async Task<IActionResult> Assign(Guid id, [FromForm] string userName, CancellationToken ct)
    {
        if (users.Find(userName) is not { } target)
        {
            return BadRequest();
        }

        var outcome = await claims.AssignAsync(id, User.Identity!.Name!, target.UserName, target.DisplayName, ct);
        TempData["Assigned"] = outcome.Result == ClaimResult.Claimed
            ? $"Kasus dialihkan ke {target.DisplayName}."
            : "Kasus ini sudah tidak menunggu keputusan, jadi tidak dialihkan.";
        return RedirectToAction(nameof(Index));
    }
}
