namespace Surveillance.Agents.Abstractions;

/// <summary>
/// The five workflow stages of asd-investigator-uma.md ("Tahap alur kerja") and the rule that
/// decides when <c>step_started</c> is emitted. Every adapter applies the same rule, so the
/// stages do not depend on the model. Mirrored in agents/langgraph/app/steps.py and agents/adk/app/steps.py.
/// </summary>
public static class InvestigationSteps
{
    public const string SubmitMemo = "submit_memo";

    public static string Label(InvestigationStep step) => step switch
    {
        InvestigationStep.Measure => "Mengukur penyimpangan",
        InvestigationStep.Explain => "Mencari penjelasan wajar",
        InvestigationStep.DeepDive => "Menggali broker dan arus asing",
        InvestigationStep.Assess => "Menilai bukti",
        InvestigationStep.Memo => "Draf memo menunggu keputusan analis",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    /// <summary>The stage a tool belongs to, or null when the tool does not move the run.</summary>
    public static InvestigationStep? ForTool(string toolName) => toolName switch
    {
        "idx_stockbit_status" or "idx_quote" or "idx_history" => InvestigationStep.Measure,
        "idx_news" or "idx_stockbit_actions" or "idx_corporate_actions" => InvestigationStep.Explain,
        "idx_broker" or "idx_bandar" or "idx_foreign" or "idx_stockbit_movements" => InvestigationStep.DeepDive,
        SubmitMemo => InvestigationStep.Assess,
        _ => null,
    };

    /// <summary>
    /// The stage to announce before an event, given the current stage, or null when the
    /// stage does not change. Stages only move forward.
    /// </summary>
    public static InvestigationStep? Next(InvestigationStep? current, AgentEventType type, string? toolName = null)
    {
        InvestigationStep? wanted = type switch
        {
            AgentEventType.RunStarted => InvestigationStep.Measure,
            AgentEventType.ToolCallStarted when toolName is not null => ForTool(toolName),
            AgentEventType.ApprovalRequired => InvestigationStep.Memo,
            _ => null,
        };

        return wanted is { } w && (current is null || w > current) ? w : null;
    }

    public static StepStartedPayload Payload(InvestigationStep step) => new(step, Label(step));
}
