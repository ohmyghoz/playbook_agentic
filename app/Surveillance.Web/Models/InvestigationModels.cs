using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Surveillance.Agents.Abstractions;
using Surveillance.Web.Data;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Models;

public class InvestigationIndexViewModel
{
    public bool LabAvailable { get; init; }

    public required List<InvestigationCase> Cases { get; init; }
    public required StartInvestigationForm Form { get; init; }

    /// <summary>The solution new investigations will use, for example "LangGraph (qwen3...)".</summary>
    public required string ActiveAgent { get; init; }
}

public class SettingsViewModel
{
    public required List<AgentChoice> Choices { get; init; }

    /// <summary>The provider key just saved, to confirm the change.</summary>
    public string? Saved { get; init; }
}

public class InvestigationDetailsViewModel
{
    public required InvestigationCase Case { get; init; }
    public bool Replay { get; init; }

    /// <summary>sse, polling or signalr (see TransportOptions).</summary>
    public string Transport { get; init; } = "sse";

    public AnomalyTrigger Trigger => JsonSerializer.Deserialize<AnomalyTrigger>(Case.TriggerJson, AgentJson.Options)!;
}

public class StartInvestigationForm
{
    [Required(ErrorMessage = "Isi kode saham.")]
    [RegularExpression("^[A-Za-z]{4}$", ErrorMessage = "Kode saham terdiri dari 4 huruf, misalnya BBCA.")]
    [Display(Name = "Kode saham")]
    public string Symbol { get; set; } = "BBCA";

    [Required(ErrorMessage = "Isi tanggal anomali.")]
    [Display(Name = "Tanggal anomali")]
    public DateOnly AnomalyDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    [Display(Name = "Jenis pemicu")]
    public TriggerKind TriggerKind { get; set; } = TriggerKind.Volume;

    [Required(ErrorMessage = "Tulis ambang yang terlampaui.")]
    [StringLength(200)]
    [Display(Name = "Aturan pemicu")]
    public string Rule { get; set; } = "Volume di atas 1,1x rata-rata 3 bulan";

    [Display(Name = "Perubahan harga (%)")]
    public double? PriceChangePct { get; set; } = 2.2;

    [Range(0, long.MaxValue)]
    [Display(Name = "Volume (lembar)")]
    public long? Volume { get; set; } = 235_635_300;

    [Range(0, double.MaxValue)]
    [Display(Name = "Rata-rata volume (lembar)")]
    public double? AvgVolume { get; set; } = 210_424_313;

    [Range(0, double.MaxValue)]
    [Display(Name = "Rasio volume")]
    public double? VolumeRatio { get; set; } = 1.12;

    [StringLength(1000)]
    [Display(Name = "Catatan untuk agent")]
    public string? Notes { get; set; }

    public StartInvestigation ToCommand() =>
        new(Symbol, AnomalyDate, TriggerKind, Rule, PriceChangePct, Volume, AvgVolume, VolumeRatio, Notes);

    public static StartInvestigationForm FromCase(InvestigationCase c)
    {
        var t = JsonSerializer.Deserialize<AnomalyTrigger>(c.TriggerJson, AgentJson.Options)!;
        return new StartInvestigationForm
        {
            Symbol = c.Symbol,
            AnomalyDate = c.AnomalyDate,
            TriggerKind = t.Kind,
            Rule = t.Rule,
            PriceChangePct = t.PriceChangePct,
            Volume = t.Volume,
            AvgVolume = t.AvgVolume,
            VolumeRatio = t.VolumeRatio,
            Notes = c.Notes,
        };
    }
}

public class ApprovalForm
{
    [Required]
    public string ApprovalId { get; set; } = "";

    [Required]
    public Decision? Decision { get; set; }

    [StringLength(2000)]
    public string? Comment { get; set; }
}

public static class CaseStatusText
{
    public static string Label(CaseStatus status) => status switch
    {
        CaseStatus.Starting => "Memulai",
        CaseStatus.Running => "Berjalan",
        CaseStatus.AwaitingApproval => "Menunggu keputusan analis",
        CaseStatus.Approved => "Memo disetujui",
        CaseStatus.Rejected => "Memo ditolak",
        CaseStatus.Cancelled => "Dihentikan",
        CaseStatus.Failed => "Gagal",
        CaseStatus.Expired => "Kedaluwarsa",
        _ => status.ToString(),
    };

    public static string DecisionLabel(string decision) => decision switch
    {
        "approve" => "Setuju",
        "revise" => "Minta revisi",
        "reject" => "Tolak",
        "expire" => "Kedaluwarsa",
        _ => decision,
    };
}

public class LabSyncViewModel
{
    public InvestigationCase? Case { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int TimeoutSeconds { get; init; }
    public List<AgentEvent> Events { get; init; } = [];
}

public class LabTransportViewModel
{
    public List<InvestigationCase> Cases { get; init; } = [];
    public Guid? CaseId { get; init; }
}

public sealed record ApprovalQueueRow(InvestigationCase Case, TimeSpan Waiting, DateTime Deadline, bool NearExpiry, ClaimState Claim);

public class ApprovalQueueViewModel
{
    public List<ApprovalQueueRow> Rows { get; init; } = [];
    public List<Surveillance.Web.Auth.DemoUser> Analysts { get; init; } = [];
    public string? Assigned { get; init; }
}

public class AuditViewModel
{
    public required InvestigationCase Case { get; init; }
    public List<AuditEntry> Entries { get; init; } = [];
}

public static class DurationText
{
    /// <summary>"2 jam 5 menit", "3 menit", "40 detik".</summary>
    public static string Of(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} jam {t.Minutes} menit";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} menit";
        return $"{t.Seconds} detik";
    }
}
