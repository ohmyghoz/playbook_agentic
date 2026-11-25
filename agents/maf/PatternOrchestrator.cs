using System.Globalization;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Agents.Maf;

/// <summary>Tool calls and time shared by all workers of one job; enforced in code, not in the prompt.</summary>
public sealed class JobBudget(int maxToolCalls, DateTime deadline, Func<int> used, Action use)
{
    private readonly object _lock = new();

    public bool Exhausted
    {
        get { lock (_lock) { return used() >= maxToolCalls || DateTime.UtcNow >= deadline; } }
    }

    /// <summary>Counts one MCP call; false when the budget was already spent.</summary>
    public bool TryUse()
    {
        lock (_lock)
        {
            if (used() >= maxToolCalls || DateTime.UtcNow >= deadline)
            {
                return false;
            }

            use();
            return true;
        }
    }

    public string Reason => DateTime.UtcNow >= deadline ? "Durasi maksimum job tercapai." : $"Batas {maxToolCalls} panggilan data per job tercapai.";
}

/// <summary>Cluster of stocks that share a dominant broker.</summary>
public sealed record ClusterFacts(string Name, IReadOnlyList<string> Stocks, IReadOnlyList<string> DominantBrokers, string? OwnershipSummary);

/// <summary>Everything the report writer receives; also the authoritative lists the report keeps.</summary>
public sealed record PatternFacts(
    DateOnly WindowStart,
    DateOnly WindowEnd,
    IReadOnlyList<ClusterFacts> Clusters,
    IReadOnlyList<WorkerOutcome> Workers,
    IReadOnlyList<Lead> LeadsNotFollowed,
    IReadOnlyList<string> LimitsReached);

/// <summary>
/// The orchestrator of asd-pattern-detection.md ("Protokol worker dan orchestrator"), as code:
/// stock workers for the candidates, broker workers for dominant brokers, stock workers for the
/// leads they find, ownership workers per cluster. All limits are enforced here. The workers
/// themselves are model agents supplied by the caller, so the same rules run on every framework.
/// Mirrored in agents/langgraph/app/pattern_orchestrator.py and agents/adk/app/pattern_orchestrator.py.
/// </summary>
public sealed class PatternOrchestrator(
    PatternJob job,
    JobBudget budget,
    Func<WorkerStartedPayload, CancellationToken, Task<WorkerOutcome>> runWorker,
    Action<string> narrate)
{
    private static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");
    private readonly List<WorkerOutcome> _outcomes = [];
    private readonly List<Lead> _notFollowed = [];
    private readonly List<string> _limits = [];
    private readonly HashSet<string> _stocks = [];
    private readonly HashSet<string> _brokers = [];

    private PatternLimits L => job.Limits;

    public async Task<PatternFacts> RunAsync(CancellationToken ct)
    {
        narrate($"Menerima {job.Candidates.Count} kandidat dari penyaringan awal untuk {job.WindowStart:yyyy-MM-dd} sampai {job.WindowEnd:yyyy-MM-dd}. ");

        var stocks = new List<WorkerStartedPayload>();
        foreach (var c in job.Candidates)
        {
            if (TakeStock(c.Symbol, out var reason))
            {
                stocks.Add(new WorkerStartedPayload($"saham-{c.Symbol}", WorkerRole.Stock, c.Symbol, 0, c.Reason));
            }
            else
            {
                _notFollowed.Add(new Lead(LeadKind.Stock, c.Symbol, reason));
            }
        }

        while (stocks.Count > 0 && !ct.IsCancellationRequested)
        {
            narrate($"Menjalankan {stocks.Count} worker saham: {string.Join(", ", stocks.Select(s => s.Target))}. ");
            var stockOutcomes = await RunWaveAsync(stocks, ct);

            var brokers = BrokersToFollow(stockOutcomes);
            if (brokers.Count == 0)
            {
                break;
            }

            narrate($"Mengikuti broker {string.Join(", ", brokers.Select(b => b.Target))}. ");
            var brokerOutcomes = await RunWaveAsync(brokers, ct);
            stocks = StockLeads(brokerOutcomes);
            if (stocks.Count > 0)
            {
                narrate($"Jejak baru dari worker broker: {string.Join(", ", stocks.Select(s => s.Target))}. ");
            }
        }

        var clusters = Clusters();
        var ownership = clusters
            .Select((c, i) => new WorkerStartedPayload($"kepemilikan-{i + 1}", WorkerRole.Ownership, string.Join(", ", c.Stocks), 1,
                $"Klaster dengan broker dominan {string.Join(", ", c.DominantBrokers)}."))
            .ToList();
        if (ownership.Count > 0)
        {
            narrate($"Memeriksa kepemilikan {ownership.Count} klaster. ");
            var owned = await RunWaveAsync(ownership, ct);
            clusters = clusters.Select((c, i) => c with { OwnershipSummary = owned.ElementAtOrDefault(i)?.Report?.Summary }).ToList();
        }

        narrate("Menyusun laporan untuk ditelaah analis. ");
        return new PatternFacts(job.WindowStart, job.WindowEnd, clusters, _outcomes, _notFollowed, _limits);
    }

    /// <summary>Runs the workers, at most three at a time; once the budget is spent, the rest do not start.</summary>
    private async Task<List<WorkerOutcome>> RunWaveAsync(List<WorkerStartedPayload> workers, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(PatternInstructions.MaxParallelWorkers);
        var tasks = workers.Select(async w =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (budget.Exhausted)
                {
                    NoteLimit(budget.Reason);
                    _notFollowed.Add(new Lead(w.Role == WorkerRole.Broker ? LeadKind.Broker : LeadKind.Stock,
                        w.Role == WorkerRole.Ownership ? w.Target.Split(',')[0].Trim() : w.Target, $"Tidak dijalankan: {budget.Reason}"));
                    return null;
                }

                return await runWorker(w, ct);
            }
            finally
            {
                gate.Release();
            }
        });
        var outcomes = (await Task.WhenAll(tasks)).OfType<WorkerOutcome>().ToList();
        lock (_outcomes)
        {
            _outcomes.AddRange(outcomes);
        }

        foreach (var o in outcomes.Where(o => o.Status == WorkerStatus.LimitReached))
        {
            NoteLimit(budget.Exhausted ? budget.Reason : $"Batas {L.MaxStepsPerWorker} langkah worker tercapai ({o.Worker.WorkerId}).");
        }

        return outcomes;
    }

    /// <summary>Dominant brokers of these stocks that the rules allow to follow (step 2 of the protocol).</summary>
    private List<WorkerStartedPayload> BrokersToFollow(List<WorkerOutcome> stockOutcomes)
    {
        var follow = new List<WorkerStartedPayload>();
        foreach (var o in stockOutcomes)
        {
            var top = (o.Report?.DominantBrokers ?? []).OrderByDescending(b => b.NetBuySharePct).Take(L.BrokersPerStock).ToList();
            if (top.Count == 0 || top.Sum(b => b.NetBuySharePct) < L.MinTopBuyerSharePct)
            {
                continue; // concentration below the screening threshold: nothing to follow
            }

            foreach (var b in top)
            {
                var code = b.Broker.Trim().ToUpperInvariant();
                if (_brokers.Contains(code) || follow.Any(f => f.Target == code))
                {
                    continue;
                }

                var spread = SpreadPct(code);
                if (spread > L.MaxBrokerSpreadPct && b.NetBuySharePct < L.MinTopBuyerSharePct)
                {
                    _notFollowed.Add(new Lead(LeadKind.Broker, code,
                        $"Dominan di {spread.ToString("0", Id)}% saham kandidat (di atas {L.MaxBrokerSpreadPct.ToString("0", Id)}%) dengan porsi beli bersih di bawah ambang."));
                    _brokers.Add(code);
                    continue;
                }

                if (o.Worker.Depth + 1 > L.MaxDepth)
                {
                    _notFollowed.Add(new Lead(LeadKind.Broker, code, $"Batas kedalaman {L.MaxDepth} tercapai."));
                    NoteLimit($"Batas kedalaman {L.MaxDepth} tercapai.");
                    continue;
                }

                _brokers.Add(code);
                follow.Add(new WorkerStartedPayload($"broker-{code}", WorkerRole.Broker, code, o.Worker.Depth + 1,
                    $"Pembeli dominan di {o.Worker.Target} ({b.NetBuySharePct.ToString("0.#", Id)}% beli bersih)."));
            }
        }

        return follow;
    }

    /// <summary>New stocks proposed by broker workers (step 3), within the stock limit.</summary>
    private List<WorkerStartedPayload> StockLeads(List<WorkerOutcome> brokerOutcomes)
    {
        var next = new List<WorkerStartedPayload>();
        foreach (var o in brokerOutcomes)
        {
            foreach (var lead in (o.Report?.Leads ?? []).Where(l => l.Kind == LeadKind.Stock))
            {
                var symbol = lead.Target.Trim().ToUpperInvariant();
                if (_stocks.Contains(symbol))
                {
                    continue;
                }

                if (TakeStock(symbol, out var reason))
                {
                    next.Add(new WorkerStartedPayload($"saham-{symbol}", WorkerRole.Stock, symbol, o.Worker.Depth, $"Jejak dari broker {o.Worker.Target}: {lead.Reason}"));
                }
                else
                {
                    _notFollowed.Add(lead with { Target = symbol, Reason = reason });
                }
            }
        }

        return next;
    }

    /// <summary>Stocks that share a followed dominant broker; a cluster needs at least two stocks.</summary>
    private List<ClusterFacts> Clusters()
    {
        var byBroker = new Dictionary<string, SortedSet<string>>();
        foreach (var o in _outcomes)
        {
            if (o.Worker.Role == WorkerRole.Stock)
            {
                foreach (var b in (o.Report?.DominantBrokers ?? []).OrderByDescending(b => b.NetBuySharePct).Take(L.BrokersPerStock))
                {
                    Add(b.Broker, o.Worker.Target);
                }
            }
            else if (o.Worker.Role == WorkerRole.Broker)
            {
                foreach (var lead in (o.Report?.Leads ?? []).Where(l => l.Kind == LeadKind.Stock && _stocks.Contains(l.Target.Trim().ToUpperInvariant())))
                {
                    Add(o.Worker.Target, lead.Target);
                }
            }
        }

        return byBroker
            .Where(kv => kv.Value.Count >= 2 && _brokers.Contains(kv.Key) && !_notFollowed.Any(n => n.Kind == LeadKind.Broker && n.Target == kv.Key))
            .Select(kv => new ClusterFacts($"Klaster {kv.Key}", kv.Value.ToList(), [kv.Key], null))
            .ToList();

        void Add(string broker, string stock)
        {
            var key = broker.Trim().ToUpperInvariant();
            if (!byBroker.TryGetValue(key, out var set))
            {
                byBroker[key] = set = [];
            }

            set.Add(stock.Trim().ToUpperInvariant());
        }
    }

    private double SpreadPct(string broker)
    {
        var candidates = job.Candidates.Select(c => c.Symbol).ToHashSet();
        var dominantIn = _outcomes.Count(o => o.Worker.Role == WorkerRole.Stock && candidates.Contains(o.Worker.Target)
            && (o.Report?.DominantBrokers ?? []).Take(L.BrokersPerStock).Any(b => b.Broker.Trim().ToUpperInvariant() == broker));
        return candidates.Count == 0 ? 0 : dominantIn * 100.0 / candidates.Count;
    }

    private bool TakeStock(string symbol, out string reason)
    {
        if (_stocks.Count >= L.MaxStocks)
        {
            reason = $"Batas {L.MaxStocks} saham per job tercapai.";
            NoteLimit(reason);
            return false;
        }

        _stocks.Add(symbol);
        reason = "";
        return true;
    }

    private void NoteLimit(string text)
    {
        lock (_limits)
        {
            if (!_limits.Contains(text))
            {
                _limits.Add(text);
            }
        }
    }
}
