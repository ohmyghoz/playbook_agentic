using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Surveillance.Agents.Abstractions;

namespace Surveillance.Web.Patterns;

public sealed record ScreenedStock(string Symbol, double PriceChangePct, DateOnly WindowStart, DateOnly WindowEnd);

public sealed record ScreeningResult(DateOnly WindowStart, DateOnly WindowEnd, List<ScreenedStock> Candidates, List<string> Notes);

/// <summary>
/// Step 1 of use case 2, as ordinary code with thresholds (not an agent): idx_screen finds stocks
/// that moved at least the threshold over a month, then idx_history measures the move over the
/// last <c>WindowDays</c> sessions; stocks that moved at least <c>MinPriceChangePct</c> either
/// way are kept, largest move first, at most <c>MaxCandidates</c>. See asd-pattern-detection.md.
/// </summary>
public sealed class CandidateScreening(IMarketData market, IOptions<PatternDetectionOptions> options, ILogger<CandidateScreening> logger)
{
    private PatternDetectionOptions O => options.Value;

    public async Task<ScreeningResult> ScreenAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var threshold = O.MinPriceChangePct.ToString(CultureInfo.InvariantCulture);
        var symbols = new List<string>();
        foreach (var (filter, ascending) in new[] { ($"ret1m>={threshold}", false), ($"ret1m<=-{threshold}", true) })
        {
            var data = await market.CallAsync("idx_screen", new Dictionary<string, object?>
            {
                ["filter"] = filter,
                ["sort"] = "ret1m",
                ["ascending"] = ascending,
                ["limit"] = O.PreScreenRows,
            }, ct);
            symbols.AddRange(data.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("symbol").GetString()!));
        }

        var kept = new List<ScreenedStock>();
        foreach (var symbol in symbols.Distinct())
        {
            try
            {
                if (await WindowMoveAsync(symbol, ct) is { } stock && Math.Abs(stock.PriceChangePct) >= O.MinPriceChangePct)
                {
                    kept.Add(stock);
                }
            }
            catch (MarketDataException ex)
            {
                logger.LogWarning(ex, "Screening skipped {Symbol}", symbol);
                notes.Add($"{symbol} dilewati: {ex.Message}");
            }
        }

        var candidates = kept.OrderByDescending(s => Math.Abs(s.PriceChangePct)).Take(O.MaxCandidates).ToList();
        if (kept.Count > candidates.Count)
        {
            notes.Add($"{kept.Count - candidates.Count} saham lain melewati ambang tetapi tidak diambil karena batas {O.MaxCandidates} kandidat.");
        }

        var start = candidates.Count > 0 ? candidates.Min(c => c.WindowStart) : DateOnly.FromDateTime(DateTime.UtcNow);
        var end = candidates.Count > 0 ? candidates.Max(c => c.WindowEnd) : start;
        return new ScreeningResult(start, end, candidates, notes);
    }

    /// <summary>Price change over the last WindowDays sessions (WindowDays + 1 closes).</summary>
    private async Task<ScreenedStock?> WindowMoveAsync(string symbol, CancellationToken ct)
    {
        var data = await market.CallAsync("idx_history", new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["period"] = "1mo",
            ["limit"] = O.WindowDays + 1,
        }, ct);
        var candles = data.GetProperty("candles").EnumerateArray().ToList();
        if (candles.Count < O.WindowDays + 1)
        {
            return null; // too few sessions, e.g. newly listed or suspended
        }

        var first = candles[^(O.WindowDays + 1)];
        var last = candles[^1];
        var change = (Close(last) / Close(first) - 1) * 100;
        return new ScreenedStock(symbol, Math.Round(change, 2), DateOf(first), DateOf(last));
    }

    private static double Close(JsonElement candle) => candle.GetProperty("close").GetDouble();

    private static DateOnly DateOf(JsonElement candle) =>
        DateOnly.FromDateTime(DateTimeOffset.Parse(candle.GetProperty("time").GetString()!, CultureInfo.InvariantCulture).Date);

    public PatternJob ToJob(ScreeningResult result) => new(result.WindowStart, result.WindowEnd,
        result.Candidates.Select(c => new PatternCandidate(c.Symbol, c.PriceChangePct,
            $"Harga {(c.PriceChangePct >= 0 ? "naik" : "turun")} {Math.Abs(c.PriceChangePct).ToString("0.#", CultureInfo.GetCultureInfo("id-ID"))}% dalam {O.WindowDays} hari bursa.")).ToList(),
        O.ToLimits());
}
