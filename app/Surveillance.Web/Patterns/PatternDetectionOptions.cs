using Surveillance.Agents.Abstractions;

namespace Surveillance.Web.Patterns;

/// <summary>
/// Thresholds and limits of use case 2 (configuration section "PatternDetection"). These are
/// demo starting values from CLAUDE.md "Ambang dan batas", not surveillance standards.
/// Screening applies the first block in code; the agent enforces <see cref="ToLimits"/> in code.
/// </summary>
public sealed class PatternDetectionOptions
{
    /// <summary>"mcp" reads the idx MCP server; "fake" serves canned data (tests, demos without MCP).</summary>
    public string MarketData { get; set; } = "mcp";

    public int WindowDays { get; set; } = 10;
    public double MinPriceChangePct { get; set; } = 15;
    public int MaxCandidates { get; set; } = 10;

    /// <summary>Rows asked from idx_screen per direction (up, down) before the 10-day check.</summary>
    public int PreScreenRows { get; set; } = 12;

    public double MinTopBuyerSharePct { get; set; } = 40;
    public double MaxBrokerSpreadPct { get; set; } = 30;
    public int MaxDepth { get; set; } = 2;
    public int MaxStocks { get; set; } = 15;
    public int BrokersPerStock { get; set; } = 3;
    public int MaxStepsPerWorker { get; set; } = 12;
    public int MaxToolCalls { get; set; } = 150;
    public int MaxMinutes { get; set; } = 20;

    public PatternLimits ToLimits() => new(MaxDepth, MaxStocks, BrokersPerStock, MaxStepsPerWorker, MaxToolCalls,
        MaxMinutes, MinTopBuyerSharePct, MaxBrokerSpreadPct);
}
