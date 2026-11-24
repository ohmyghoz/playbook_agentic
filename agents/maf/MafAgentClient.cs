using System.ClientModel;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

/// <summary>
/// Solution 1: the UMA investigator on Microsoft Agent Framework, in process.
/// Maps MAF streaming updates to the event catalog; see agents/maf/EVENT-MAPPING.md.
/// </summary>
public sealed class MafAgentClient : IAgentClient
{
    private const string RevisePrefix = "Analis meminta revisi: ";
    private const string ContinueMessage = "Lanjutkan: panggil tool berikutnya, atau panggil submit_memo jika data sudah cukup.";
    private const int MaxTextOnlyTurns = 3;
    private const int MaxInvalidMemoAttempts = 3;

    private readonly MafAgentOptions _options;
    private readonly IChatClient _chatClient;
    private readonly IToolSource _tools;
    private readonly RunStore _store;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public MafAgentClient(MafAgentOptions options, IChatClient chatClient, IToolSource tools, ILogger<MafAgentClient>? logger = null)
    {
        _options = options;
        _chatClient = chatClient;
        _tools = tools;
        _store = new RunStore(options.StateDirectory);
        _logger = logger ?? NullLogger<MafAgentClient>.Instance;
    }

    public Task<string> StartAsync(InvestigationRequest request, CancellationToken ct)
    {
        var runId = $"maf-{Guid.NewGuid():N}";
        _store.Save(new RunState { RunId = runId, Request = request });
        return Task.FromResult(runId);
    }

    public async Task ResumeAsync(string runId, ApprovalDecision decision, CancellationToken ct)
    {
        var gate = Gate(runId);
        await gate.WaitAsync(ct);
        try
        {
            var state = _store.Load(runId);
            if (state.Phase != RunPhase.Paused || state.Pending?.ApprovalId != decision.ApprovalId)
            {
                throw new InvalidOperationException($"Run {runId} is not waiting for approval {decision.ApprovalId}.");
            }

            state.Decision = decision;
            state.Phase = RunPhase.Resumed;
            _store.Save(state);
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(string runId, [EnumeratorCancellation] CancellationToken ct)
    {
        var gate = Gate(runId);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var state = _store.Load(runId);
            var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });
            var producer = Task.Run(() => ProduceAsync(state, channel.Writer, ct), CancellationToken.None);

            // Read without the token: after a cancel the producer still writes the closing events.
            await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return e;
            }

            await producer;
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim Gate(string runId) => _gates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));

    private async Task ProduceAsync(RunState state, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        var emit = new Emitter(state, writer);
        try
        {
            switch (state.Phase)
            {
                case RunPhase.Finished:
                    return;
                case RunPhase.Paused:
                    if (ct.IsCancellationRequested)
                    {
                        emit.Cancel();
                    }

                    return;
                case RunPhase.Running:
                    // A previous stream was interrupted (process stopped mid-run); the session is not consistent.
                    emit.CloseOpenCalls("internal", "Run terputus.");
                    emit.Error(AgentErrorCode.Internal, "Investigasi terputus di tengah jalan. Mulai investigasi baru untuk mencoba lagi.", true);
                    return;
            }

            var input = state.Phase == RunPhase.New ? StartInput(state, emit) : ResumeInput(state, emit);
            if (input is null)
            {
                return; // the decision ended the run
            }

            state.Phase = RunPhase.Running;
            IReadOnlyList<AITool> mcpTools;
            try
            {
                mcpTools = await _tools.GetToolsAsync(ct);
            }
            catch (McpUnavailableException ex)
            {
                _logger.LogError(ex, "MCP unavailable for run {RunId}", state.RunId);
                emit.CloseOpenCalls("mcp_unavailable", "Server MCP tidak tersedia.");
                emit.Error(AgentErrorCode.McpUnavailable, "Server data pasar tidak bisa dihubungi. Coba lagi dalam beberapa menit.", true);
                return;
            }

            var limits = new ToolLimits(state, _options.MaxToolCalls);
            var agent = BuildAgent(state.Request, mcpTools, limits);
            var session = state.Session is { } saved
                ? await agent.DeserializeSessionAsync(saved, null, ct)
                : await agent.CreateSessionAsync(ct);

            await RunUntilPauseOrEndAsync(agent, session, input, state, limits, emit, ct);
            state.Session = await agent.SerializeSessionAsync(session, null, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            emit.Cancel();
        }
        catch (ClientResultException ex)
        {
            _logger.LogError(ex, "Model call failed for run {RunId}", state.RunId);
            emit.CloseOpenCalls("model_error", "Model gagal merespons.");
            emit.Error(AgentErrorCode.ModelError, "Model AI sedang tidak merespons. Coba lagi dalam beberapa menit.", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MAF run {RunId} failed", state.RunId);
            emit.CloseOpenCalls("internal", "Terjadi kesalahan.");
            emit.Error(AgentErrorCode.Internal, "Terjadi kesalahan pada agent. Mulai investigasi baru untuk mencoba lagi.", false);
        }
        finally
        {
            emit.FlushTokens();
            _store.Save(state);
            writer.TryComplete();
        }
    }

    private List<ChatMessage> StartInput(RunState state, Emitter emit)
    {
        emit.Event(AgentEventType.RunStarted, new RunStartedPayload(state.Request.CaseId, state.Request.Symbol, _options.MaxToolCalls));
        return [new ChatMessage(ChatRole.User, InvestigatorInstructions.FirstMessage(state.Request))];
    }

    /// <summary>Turns the analyst's decision into the next model input, or ends the run.</summary>
    private List<ChatMessage>? ResumeInput(RunState state, Emitter emit)
    {
        var pending = state.Pending!;
        var decision = state.Decision!;
        var request = (ToolApprovalRequestContent)JsonSerializer.Deserialize<AIContent>(pending.RequestJson, AIJsonUtilities.DefaultOptions)!;
        var others = pending.OtherApprovalsJson
            .Select(j => JsonSerializer.Deserialize<AIContent>(j, AIJsonUtilities.DefaultOptions)!)
            .ToList();
        state.Pending = null;
        state.Decision = null;

        switch (decision.Decision)
        {
            case Decision.Approve:
                state.AwaitingApprovedSubmit = true;
                return [new ChatMessage(ChatRole.User, [request.CreateResponse(true), .. others])];

            case Decision.Reject:
                emit.Finished(pending.CallId, ToolCallStatus.Ok, "Memo ditolak analis.");
                emit.Done(RunOutcome.Rejected);
                return null;

            case Decision.Revise when state.Revision >= _options.MaxRevisions:
                emit.Finished(pending.CallId, ToolCallStatus.Ok, "Revisi diminta, tetapi batas revisi sudah tercapai.");
                emit.Error(AgentErrorCode.RevisionLimitExceeded,
                    $"Memo sudah direvisi {_options.MaxRevisions} kali. Setujui atau tolak draf terakhir.", false);
                return null;

            default:
                emit.Finished(pending.CallId, ToolCallStatus.Ok, "Analis meminta revisi.");
                return [new ChatMessage(ChatRole.User, [request.CreateResponse(false, RevisePrefix + decision.Comment), .. others])];
        }
    }

    private AIAgent BuildAgent(InvestigationRequest request, IReadOnlyList<AITool> mcpTools, ToolLimits limits)
    {
        var submitMemo = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
            (Memo memo) => "Memo disetujui analis.",
            InvestigatorInstructions.SubmitMemo,
            "Kirim draf memo investigasi untuk ditelaah analis. Analis bisa menyetujui, meminta revisi, atau menolak.",
            AgentJson.Options));

        var agent = new ChatClientAgent(
            _chatClient,
            instructions: InvestigatorInstructions.Build(request, _options.MaxToolCalls),
            name: "investigator-uma",
            tools: [.. mcpTools, submitMemo]);

        return agent.AsBuilder().Use(limits.InvokeAsync).Build();
    }

    /// <summary>
    /// Streams model passes until the run pauses for the analyst or ends.
    /// Approval requests for MCP tools are answered automatically; only
    /// submit_memo goes to the analyst.
    /// </summary>
    private async Task RunUntilPauseOrEndAsync(AIAgent agent, AgentSession session, List<ChatMessage> input,
        RunState state, ToolLimits limits, Emitter emit, CancellationToken ct)
    {
        // Some models narrate in one turn and call the tool in the next; count consecutive text-only turns.
        var textOnlyTurns = 0;
        while (true)
        {
            var callsBefore = state.StartedCallIds.Count;
            var autoApprovals = new List<AIContent>();
            ToolApprovalRequestContent? submitRequest = null;
            var submitExecuted = false;
            limits.BeginPass();

            await foreach (var update in agent.RunStreamingAsync(input, session, null, ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    emit.Token(update.Text);
                }

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case ToolApprovalRequestContent approval when approval.ToolCall is FunctionCallContent call:
                            if (call.Name == InvestigatorInstructions.SubmitMemo)
                            {
                                submitRequest = approval;
                            }
                            else
                            {
                                emit.Started(call);
                                autoApprovals.Add(approval.CreateResponse(true));
                            }

                            break;

                        case FunctionCallContent call when call.Name != InvestigatorInstructions.SubmitMemo:
                            emit.Started(call);
                            break;

                        case FunctionResultContent result:
                            var name = state.CallNames.GetValueOrDefault(result.CallId, "unknown");
                            if (name == InvestigatorInstructions.SubmitMemo)
                            {
                                submitExecuted = true;
                            }

                            emit.Finished(ToolResultSummarizer.Summarize(result.CallId, name, result));
                            break;
                    }
                }

                // The analyst has approved: the run is over, so do not let the model write a closing reply.
                if (submitExecuted && state.AwaitingApprovedSubmit)
                {
                    break;
                }
            }

            if (limits.Exceeded)
            {
                emit.CloseOpenCalls("step_limit", "Batas panggilan data tercapai.");
                emit.Error(AgentErrorCode.StepLimitExceeded,
                    $"Agent mencapai batas {_options.MaxToolCalls} panggilan data tanpa menyusun memo.", false);
                return;
            }

            if (state.AwaitingApprovedSubmit && submitExecuted)
            {
                state.AwaitingApprovedSubmit = false;
                emit.Done(RunOutcome.Approved);
                return;
            }

            if (submitRequest is not null)
            {
                var call = (FunctionCallContent)submitRequest.ToolCall;
                var mcpCallIds = state.StartedCallIds.Where(id => state.CallNames[id] != InvestigatorInstructions.SubmitMemo).ToHashSet();
                var (memo, problem) = MemoValidator.Validate(call, state.Request, mcpCallIds);
                emit.Started(call);

                if (memo is null)
                {
                    if (++state.InvalidMemoAttempts > MaxInvalidMemoAttempts)
                    {
                        emit.Finished(call.CallId, ToolCallStatus.Error, "Memo tidak sesuai format.", new ToolError("invalid_memo", problem!));
                        emit.CloseOpenCalls("invalid_memo", "Memo tidak sesuai format.");
                        emit.Error(AgentErrorCode.ModelError, "Agent tidak berhasil menyusun memo yang sesuai format. Coba lagi.", true);
                        return;
                    }

                    emit.Finished(call.CallId, ToolCallStatus.Error, "Memo belum sesuai format; agent memperbaikinya.", new ToolError("invalid_memo", problem!));
                    input = [new ChatMessage(ChatRole.User, [submitRequest.CreateResponse(false, problem), .. autoApprovals])];
                    continue;
                }

                state.Revision++;
                state.Memo = memo;
                var approvalId = $"approval_{state.Revision + 1}";
                state.Pending = new PendingApproval(approvalId, call.CallId,
                    JsonSerializer.Serialize<AIContent>(submitRequest, AIJsonUtilities.DefaultOptions),
                    autoApprovals.Select(a => JsonSerializer.Serialize(a, AIJsonUtilities.DefaultOptions)).ToList());
                emit.Event(AgentEventType.ApprovalRequired, new ApprovalRequiredPayload(approvalId, state.Revision, memo));
                state.Phase = RunPhase.Paused;
                return;
            }

            if (autoApprovals.Count > 0)
            {
                input = [new ChatMessage(ChatRole.User, autoApprovals)];
                continue;
            }

            // The pass ended with a reply that called no tool: its last turn was text only.
            textOnlyTurns = state.StartedCallIds.Count > callsBefore ? 1 : textOnlyTurns + 1;
            if (textOnlyTurns < MaxTextOnlyTurns)
            {
                input = [new ChatMessage(ChatRole.User, ContinueMessage)];
                continue;
            }

            emit.CloseOpenCalls("model_error", "Agent berhenti tanpa memo.");
            emit.Error(AgentErrorCode.ModelError, "Agent berhenti tanpa menyusun memo. Coba lagi.", true);
            return;
        }
    }

    /// <summary>Numbers events, batches text deltas, and keeps tool-call pairing consistent.</summary>
    private sealed class Emitter(RunState state, ChannelWriter<AgentEvent> writer)
    {
        // Model deltas are often a few characters; batching keeps the event log readable and small.
        private const int TokenBatchChars = 60;
        private readonly System.Text.StringBuilder _tokens = new();

        public void Token(string text)
        {
            _tokens.Append(text);
            if (_tokens.Length >= TokenBatchChars)
            {
                FlushTokens();
            }
        }

        public void FlushTokens()
        {
            if (_tokens.Length == 0 || state.Phase == RunPhase.Finished)
            {
                return;
            }

            var text = _tokens.ToString();
            _tokens.Clear();
            writer.TryWrite(AgentEvent.Create(state.RunId, ++state.Sequence, AgentEventType.Token, new TokenPayload(text)));
        }

        public void Event<T>(AgentEventType type, T payload)
        {
            if (state.Phase == RunPhase.Finished)
            {
                return;
            }

            FlushTokens();

            writer.TryWrite(AgentEvent.Create(state.RunId, ++state.Sequence, type, payload));
            if (type is AgentEventType.Done or AgentEventType.Error)
            {
                state.Phase = RunPhase.Finished;
            }
        }

        public void Started(FunctionCallContent call)
        {
            if (!state.StartedCallIds.Add(call.CallId))
            {
                return;
            }

            state.CallNames[call.CallId] = call.Name;
            var args = call.Name == InvestigatorInstructions.SubmitMemo
                ? JsonSerializer.SerializeToElement(new { memo = "(lihat approval_required)" })
                : JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions);
            Event(AgentEventType.ToolCallStarted, new ToolCallStartedPayload(call.CallId, call.Name, args));
        }

        public void Finished(ToolCallFinishedPayload payload)
        {
            if (!state.StartedCallIds.Contains(payload.ToolCallId) || !state.FinishedCallIds.Add(payload.ToolCallId))
            {
                return;
            }

            Event(AgentEventType.ToolCallFinished, payload);
        }

        public void Finished(string callId, ToolCallStatus status, string summary, ToolError? error = null) =>
            Finished(new ToolCallFinishedPayload(callId, state.CallNames.GetValueOrDefault(callId, "unknown"), status, summary, error));

        public void CloseOpenCalls(string code, string message)
        {
            foreach (var id in state.OpenCallIds.ToList())
            {
                Finished(id, ToolCallStatus.Error, message, new ToolError(code, message));
            }
        }

        public void Done(RunOutcome outcome) =>
            Event(AgentEventType.Done, new DonePayload(outcome, state.ToolCallsUsed,
                outcome == RunOutcome.Cancelled ? null : state.Memo));

        public void Error(AgentErrorCode code, string message, bool retryable) =>
            Event(AgentEventType.Error, new ErrorPayload(code, message, retryable));

        public void Cancel()
        {
            CloseOpenCalls("cancelled", "Dihentikan oleh pengguna.");
            Done(RunOutcome.Cancelled);
        }
    }
}
