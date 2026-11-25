namespace Surveillance.Web.Data;

public static class PatternJobs
{
    /// <summary>The wire name of <c>UseCase.PatternDetection</c>, stored in <see cref="InvestigationCase.UseCase"/>.</summary>
    public const string UseCaseName = "pattern_detection";

    /// <summary>Jobs cover many stocks; this fills the case's Symbol column.</summary>
    public const string JobSymbol = "POLA";
}
