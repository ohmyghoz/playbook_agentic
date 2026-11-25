using System.ClientModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

/// <summary>
/// Use case 2 on Microsoft Agent Framework: <see cref="PatternOrchestrator"/> decides which workers
/// run; each worker is a <see cref="ChatClientAgent"/> with its role's MCP tools and finish_worker;
/// a report agent with only submit_report (an <see cref="ApprovalRequiredAIFunction"/>) writes the
/// report and pauses for the analyst, like submit_memo in the UMA investigator.
/// See agents/maf/EVENT-MAPPING.md.
/// </summary>
internal sealed class PatternRunner(MafAgentOptions options, IChatClient chatClient, IToolSource tools, ILogger logger)
{
    private const string ContinueMessage = "Panggil finish_worker sekarang dengan temuan yang sudah ada.";
    private const int MaxInvalidReports = 3;

    public async Task ProduceAsync(RunState state, MafAgentClient.Emitter emit, CancellationToken ct)
    {
        var job = state.Request.Job!;
        List<ChatMessage>? input;
        if (state.Phase == RunPhase.New)
        {
            emit.Event(AgentEventType.RunStarted, new RunStartedPayload(state.Request.CaseId, null, job.Limits.MaxToolCalls, UseCase.PatternDetection));
            state.Phase = RunPhase.Running;
            var budget = new JobBudget(job.Limits.MaxToolCalls, DateTime.UtcNow.AddMinutes(job.Limits.MaxMinutes),
                () => state.ToolCallsUsed, () => state.ToolCallsUsed++);
            var orchestrator = new PatternOrchestrator(job, budget, (w, token) => RunWorkerAsync(w, job, budget, state, emit, token), emit.Token);
            var facts = await orchestrator.RunAsync(ct);
            state.PatternFactsJson = JsonSerializer.Serialize(facts, AgentJson.Options);
            input = [new ChatMessage(ChatRole.User, "Hasil worker:\n" + state.PatternFactsJson)];
        }
        else
        {
            input = ResumeInput(state, emit);
            if (input is null)
            {
                return;
            }

            state.Phase = RunPhase.Running;
        }

        await ReportLoopAsync(state, emit, input, ct);
    }

    // ---- workers ----

    private async Task<WorkerOutcome> RunWorkerAsync(WorkerStartedPayload worker, PatternJob job, JobBudget budget,
        RunState state, MafAgentClient.Emitter emit, CancellationToken ct)
    {
        emit.WorkerStarted(worker);
        WorkerReport? report = null;
        var steps = 0;
        var warned = false;
        var exceeded = false;

        var finish = AIFunctionFactory.Create(
            (string summary, List<BrokerShare>? dominantBrokers, List<Lead>? leads, List<Statement>? findings) =>
            {
                // Models sometimes fill leads loosely; only complete ones reach the orchestrator.
                var clean = leads?.Where(l => !string.IsNullOrWhiteSpace(l.Target))
                    .Select(l => l with { Target = l.Target.Trim().ToUpperInvariant() }).ToList();
                report = new WorkerReport(summary, dominantBrokers?.Where(b => !string.IsNullOrWhiteSpace(b.Broker)).ToList(), clean, findings);
                return "Temuan diterima.";
            },
            PatternInstructions.FinishWorker,
            "Laporkan temuan worker ke orchestrator. Panggil tepat satu kali di akhir tugas.",
            AgentJson.Options);

        async ValueTask<object?> Limit(AIAgent _, FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken token)
        {
            if (context.Function.Name == PatternInstructions.FinishWorker)
            {
                var done = await next(context, token);
                context.Terminate = true; // the worker's job is over; no closing reply needed
                return done;
            }

            if (steps >= job.Limits.MaxStepsPerWorker || !budget.TryUse())
            {
                if (warned)
                {
                    exceeded = true;
                    context.Terminate = true;
                }

                warned = true;
                return $"{ToolLimits.LimitMarker} Batas panggilan tool tercapai. Panggil finish_worker sekarang dengan temuan yang ada.";
            }

            steps++;
            var result = await next(context, token);
            return $"[toolCallId: {context.CallContent.CallId}]\n{ToolResultSummarizer.ResultText(result)}";
        }

        var status = WorkerStatus.Error;
        var fallback = "Worker berhenti tanpa melaporkan temuan.";
        try
        {
            var mcpTools = await tools.GetToolsAsync(PatternInstructions.ToolsFor(worker.Role), ct);
            var agent = new ChatClientAgent(chatClient,
                    instructions: PatternInstructions.Worker(worker.Role, worker.Target, job),
                    name: worker.WorkerId,
                    tools: [.. mcpTools, finish])
                .AsBuilder().Use(Limit).Build();
            var session = await agent.CreateSessionAsync(ct);
            List<ChatMessage> input = [new(ChatRole.User, "Mulai tugas Anda.")];

            // The worker gets two reminders to call finish_worker before it counts as failed.
            for (var attempt = 0; attempt < 3 && report is null && !exceeded; attempt++)
            {
                await foreach (var update in agent.RunStreamingAsync(input, session, null, ct))
                {
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case FunctionCallContent call when call.Name != PatternInstructions.FinishWorker:
                                emit.Started(call, worker.WorkerId);
                                break;
                            case FunctionResultContent result:
                                emit.Finished(ToolResultSummarizer.Summarize(result.CallId,
                                    state.CallNames.GetValueOrDefault(result.CallId, "unknown"), result));
                                break;
                        }
                    }
                }

                input = [new ChatMessage(ChatRole.User, ContinueMessage)];
            }

            // A worker that ran into its step limit or the job budget stops at limit_reached, findings or not.
            status = warned || exceeded ? WorkerStatus.LimitReached : report is not null ? WorkerStatus.Ok : WorkerStatus.Error;
            if (exceeded && report is null)
            {
                fallback = $"Batas {job.Limits.MaxStepsPerWorker} langkah worker tercapai sebelum temuan dilaporkan.";
            }
        }
        catch (Exception ex) when (ex is ClientResultException or McpUnavailableException)
        {
            // A failed worker does not fail the job (asd-pattern-detection.md); the report notes it.
            logger.LogWarning(ex, "Worker {WorkerId} failed", worker.WorkerId);
            fallback = "Worker gagal: model atau server data pasar tidak merespons.";
        }

        emit.CloseOpenCalls(exceeded ? "step_limit" : "internal", "Panggilan tidak selesai.", worker.WorkerId);
        var summary = report?.Summary ?? fallback;
        emit.WorkerFinished(new WorkerFinishedPayload(worker.WorkerId, worker.Role, status,
            summary.Length <= 500 ? summary : summary[..497] + "...", report?.Leads));
        return new WorkerOutcome(worker, status, report);
    }

    // ---- report ----

    private AIAgent ReportAgent() => new ChatClientAgent(chatClient,
        instructions: PatternInstructions.ReportTemplate,
        name: "penyusun-laporan",
        tools:
        [
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                (PatternReport report) => "Laporan disetujui analis.",
                PatternInstructions.SubmitReport,
                "Ajukan laporan deteksi pola untuk ditelaah analis. Analis bisa menyetujui, meminta revisi, atau menolak.",
                AgentJson.Options)),
        ]);

    private async Task ReportLoopAsync(RunState state, MafAgentClient.Emitter emit, List<ChatMessage> input, CancellationToken ct)
    {
        var agent = ReportAgent();
        var session = state.Session is { } saved
            ? await agent.DeserializeSessionAsync(saved, null, ct)
            : await agent.CreateSessionAsync(ct);
        var textOnly = 0;

        while (true)
        {
            ToolApprovalRequestContent? submitRequest = null;
            var submitExecuted = false;
            await foreach (var update in agent.RunStreamingAsync(input, session, null, ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    emit.Token(update.Text);
                }

                foreach (var content in update.Contents)
                {
                    if (content is ToolApprovalRequestContent { ToolCall: FunctionCallContent { Name: PatternInstructions.SubmitReport } } approval)
                    {
                        submitRequest = approval;
                    }
                    else if (content is FunctionResultContent result && state.CallNames.GetValueOrDefault(result.CallId) == PatternInstructions.SubmitReport)
                    {
                        submitExecuted = true;
                    }
                }

                if (submitExecuted && state.AwaitingApprovedSubmit)
                {
                    break; // approved: no closing reply needed
                }
            }

            if (state.AwaitingApprovedSubmit && submitExecuted)
            {
                state.AwaitingApprovedSubmit = false;
                emit.Finished(state.StartedCallIds.Last(id => state.CallNames[id] == PatternInstructions.SubmitReport),
                    ToolCallStatus.Ok, "Laporan disetujui analis.");
                emit.Done(RunOutcome.Approved);
                break;
            }

            if (submitRequest is not null)
            {
                var call = (FunctionCallContent)submitRequest.ToolCall;
                emit.Started(call);
                var (report, problem) = ValidateReport(call, state);
                if (report is null)
                {
                    if (++state.InvalidReportAttempts > MaxInvalidReports)
                    {
                        emit.Finished(call.CallId, ToolCallStatus.Error, "Laporan tidak sesuai format.", new ToolError("invalid_report", problem!));
                        emit.Error(AgentErrorCode.ModelError, "Agent tidak berhasil menyusun laporan yang sesuai format. Coba lagi.", true);
                        break;
                    }

                    emit.Finished(call.CallId, ToolCallStatus.Error, "Laporan belum sesuai format; agent memperbaikinya.", new ToolError("invalid_report", problem!));
                    input = [new ChatMessage(ChatRole.User, [submitRequest.CreateResponse(false, problem)])];
                    continue;
                }

                state.Revision++;
                state.Report = report;
                var approvalId = $"approval_{state.Revision + 1}";
                state.Pending = new PendingApproval(approvalId, call.CallId,
                    JsonSerializer.Serialize<AIContent>(submitRequest, AIJsonUtilities.DefaultOptions), []);
                emit.Event(AgentEventType.ApprovalRequired, new ApprovalRequiredPayload(approvalId, state.Revision, null, report));
                state.Phase = RunPhase.Paused;
                break;
            }

            if (++textOnly >= 3)
            {
                emit.Error(AgentErrorCode.ModelError, "Agent berhenti tanpa menyusun laporan. Coba lagi.", true);
                break;
            }

            input = [new ChatMessage(ChatRole.User, "Lanjutkan: panggil submit_report dengan laporan lengkap.")];
        }

        state.Session = await agent.SerializeSessionAsync(session, null, CancellationToken.None);
    }

    /// <summary>
    /// Reads the report the model submitted. The orchestrator's own lists (period, counts, leads not
    /// followed, limits) are authoritative and replace the model's; evidence must cite real tool calls.
    /// </summary>
    private static (PatternReport? Report, string? Problem) ValidateReport(FunctionCallContent call, RunState state)
    {
        PatternReport? report;
        try
        {
            object? raw = null;
            call.Arguments?.TryGetValue("report", out raw);
            report = JsonSerializer.SerializeToElement(raw, AIJsonUtilities.DefaultOptions).Deserialize<PatternReport>(AgentJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, $"Laporan tidak bisa dibaca: {ex.Message}");
        }

        if (report is null || string.IsNullOrWhiteSpace(report.Summary))
        {
            return (null, "Laporan kosong. Isi summary, clusters, dan bagian lain sesuai skema.");
        }

        report = Normalize(report);

        var facts = JsonSerializer.Deserialize<PatternFacts>(state.PatternFactsJson!, AgentJson.Options)!;
        var known = state.StartedCallIds.Where(id => state.CallAgents.ContainsKey(id)).ToHashSet();
        var cited = report.Clusters.SelectMany(c => c.Findings).Concat(report.AlternativeExplanations)
            .SelectMany(s => s.Evidence).Select(e => e.ToolCallId).Where(id => !known.Contains(id)).Distinct().ToList();
        if (cited.Count > 0)
        {
            return (null, $"toolCallId berikut tidak berasal dari worker: {string.Join(", ", cited)}. Rujuk hanya toolCallId dari hasil worker.");
        }

        // Clusters are the orchestrator's (stocks sharing a followed broker); the model may not invent them.
        static string Key(IEnumerable<string> stocks) => string.Join(",", stocks.Select(s => s.Trim().ToUpperInvariant()).Order());
        var expected = facts.Clusters.Select(c => Key(c.Stocks)).ToHashSet();
        if (report.Clusters.Count != expected.Count || report.Clusters.Any(c => !expected.Contains(Key(c.Stocks))))
        {
            var list = facts.Clusters.Count == 0 ? "tidak ada" : string.Join("; ", facts.Clusters.Select(c => string.Join(", ", c.Stocks)));
            return (null, $"clusters harus sama dengan klaster dari orchestrator ({list}). Bila tidak ada klaster, isi clusters dengan daftar kosong dan tulis pengamatan per saham di summary dan alternativeExplanations.");
        }

        var stocks = report.Clusters.SelectMany(c => c.Stocks).Distinct().Count();
        var brokers = report.Clusters.SelectMany(c => c.DominantBrokers).Distinct().Count();
        return (report with
        {
            PeriodStart = facts.WindowStart,
            PeriodEnd = facts.WindowEnd,
            Counts = new ReportCounts(report.Clusters.Count, stocks, brokers),
            LeadsNotFollowed = facts.LeadsNotFollowed,
            LimitsReached = facts.LimitsReached,
            Disclaimer = Memo.StandardDisclaimer,
        }, null);
    }

    /// <summary>Lists the model left out arrive as null despite the record's types; the schema wants them empty.</summary>
    private static PatternReport Normalize(PatternReport r) => r with
    {
        Clusters = (r.Clusters ?? []).Select(c => c with
        {
            Stocks = c.Stocks ?? [],
            DominantBrokers = c.DominantBrokers ?? [],
            Findings = (c.Findings ?? []).Select(f => f with { Evidence = f.Evidence ?? [] }).ToList(),
        }).ToList(),
        AlternativeExplanations = (r.AlternativeExplanations ?? []).Select(f => f with { Evidence = f.Evidence ?? [] }).ToList(),
        DataLimitations = r.DataLimitations ?? [],
        FollowUps = r.FollowUps ?? [],
        LeadsNotFollowed = r.LeadsNotFollowed ?? [],
        LimitsReached = r.LimitsReached ?? [],
    };

    /// <summary>The analyst's decision as the report agent's next input, or null when it ends the run.</summary>
    private List<ChatMessage>? ResumeInput(RunState state, MafAgentClient.Emitter emit)
    {
        var pending = state.Pending!;
        var decision = state.Decision!;
        var request = (ToolApprovalRequestContent)JsonSerializer.Deserialize<AIContent>(pending.RequestJson, AIJsonUtilities.DefaultOptions)!;
        state.Pending = null;
        state.Decision = null;

        switch (decision.Decision)
        {
            case Decision.Approve:
                state.AwaitingApprovedSubmit = true;
                return [new ChatMessage(ChatRole.User, [request.CreateResponse(true)])];

            case Decision.Reject:
                emit.Finished(pending.CallId, ToolCallStatus.Ok, "Laporan ditolak analis.");
                emit.Done(RunOutcome.Rejected);
                return null;

            case Decision.Revise when state.Revision >= options.MaxRevisions:
                emit.Finished(pending.CallId, ToolCallStatus.Ok, "Revisi diminta, tetapi batas revisi sudah tercapai.");
                emit.Error(AgentErrorCode.RevisionLimitExceeded,
                    $"Laporan sudah direvisi {options.MaxRevisions} kali. Setujui atau tolak draf terakhir.", false);
                return null;

            default:
                emit.Finished(pending.CallId, ToolCallStatus.Ok, "Analis meminta revisi.");
                return [new ChatMessage(ChatRole.User, [request.CreateResponse(false, "Analis meminta revisi: " + decision.Comment)])];
        }
    }
}
