using System.Text.Json;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

internal enum RunPhase
{
    New,
    Running,
    Paused,
    Resumed,
    Finished,
}

/// <summary>
/// What the adapter needs to continue a run: the serialized MAF session and
/// the bookkeeping for sequence numbers, limits, and the pending approval.
/// Not a source of truth; the application database is.
/// </summary>
internal sealed class RunState
{
    public required string RunId { get; init; }
    public required InvestigationRequest Request { get; init; }
    public RunPhase Phase { get; set; } = RunPhase.New;
    public long Sequence { get; set; }
    public int ToolCallsUsed { get; set; }
    public bool LimitWarned { get; set; }
    public int Revision { get; set; } = -1;
    public int InvalidMemoAttempts { get; set; }
    public HashSet<string> StartedCallIds { get; set; } = [];
    public HashSet<string> FinishedCallIds { get; set; } = [];
    public Dictionary<string, string> CallNames { get; set; } = [];
    public JsonElement? Session { get; set; }
    public PendingApproval? Pending { get; set; }
    public ApprovalDecision? Decision { get; set; }
    public Memo? Memo { get; set; }

    /// <summary>True after the analyst approved and before submit_memo's result arrived.</summary>
    public bool AwaitingApprovedSubmit { get; set; }

    public IEnumerable<string> OpenCallIds => StartedCallIds.Except(FinishedCallIds);
}

/// <param name="RequestJson">The ToolApprovalRequestContent, serialized with AIJsonUtilities.DefaultOptions.</param>
/// <param name="OtherApprovalsJson">Auto-approvals for MCP calls that arrived in the same response.</param>
internal sealed record PendingApproval(string ApprovalId, string CallId, string RequestJson, List<string> OtherApprovalsJson);

/// <summary>One JSON file per run, written atomically.</summary>
internal sealed class RunStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new(AgentJson.Options) { WriteIndented = true };

    public void Save(RunState state)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(state.RunId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        File.Move(temp, path, overwrite: true);
    }

    public RunState Load(string runId)
    {
        var path = PathFor(runId);
        if (!File.Exists(path))
        {
            throw new KeyNotFoundException($"Unknown run {runId}.");
        }

        return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), Options)
               ?? throw new InvalidDataException($"Run state {runId} is empty.");
    }

    private string PathFor(string runId)
    {
        if (runId.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
        {
            throw new ArgumentException("Invalid run id.", nameof(runId));
        }

        return Path.Combine(directory, runId + ".json");
    }
}
