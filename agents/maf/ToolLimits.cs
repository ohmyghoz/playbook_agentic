using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Surveillance.Agents.Maf;

/// <summary>
/// Function-calling middleware that enforces the ASD step limit (MCP tool
/// calls per run) and tags every tool result with its call id so the model
/// can cite it as evidence.
/// </summary>
internal sealed class ToolLimits(RunState state, int maxToolCalls)
{
    public const string LimitMarker = "[batas-panggilan-tool]";

    // The model turn that first received the limit warning, as (pass, iteration). Calls from that
    // same turn were requested in parallel before the model could read the warning.
    private (int Pass, int Iteration)? _warnedAt;
    private int _pass;

    /// <summary>Set when the model asked for a tool again after being told the limit was reached.</summary>
    public bool Exceeded { get; private set; }

    /// <summary>Call before each RunStreamingAsync pass; FunctionInvocationContext.Iteration restarts per pass.</summary>
    public void BeginPass() => _pass++;

    public async ValueTask<object?> InvokeAsync(AIAgent agent, FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken ct)
    {
        if (context.Function.Name == InvestigatorInstructions.SubmitMemo)
        {
            return await next(context, ct);
        }

        if (state.ToolCallsUsed >= maxToolCalls)
        {
            var turn = (_pass, context.Iteration);
            if (state.LimitWarned && _warnedAt != turn)
            {
                Exceeded = true;
                context.Terminate = true;
            }

            state.LimitWarned = true;
            _warnedAt ??= turn;
            return $"{LimitMarker} Batas {maxToolCalls} panggilan tool tercapai. Susun memo dari data yang sudah ada.";
        }

        state.ToolCallsUsed++;
        var result = await next(context, ct);
        return $"[toolCallId: {context.CallContent.CallId}]\n{ToolResultSummarizer.ResultText(result)}";
    }
}
