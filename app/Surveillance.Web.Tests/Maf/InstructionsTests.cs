using System.Reflection;
using System.Text.RegularExpressions;
using Surveillance.Agents.Maf;
using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests.Maf;

/// <summary>The three solutions must use the same system instructions as the ASD.</summary>
public partial class InstructionsTests
{
    [Fact]
    public void Template_matches_the_asd()
    {
        var asd = File.ReadAllText(RepoPaths.Contracts("asd-investigator-uma.md")).Replace("\r\n", "\n");
        var section = asd[asd.IndexOf("## Instruksi sistem", StringComparison.Ordinal)..];
        var expected = TextBlock().Match(section).Groups[1].Value;

        var template = (string)typeof(InvestigatorInstructions)
            .GetField("Template", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(expected, template.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("### Instruksi worker", nameof(PatternInstructions.WorkerTemplate))]
    [InlineData("### Instruksi penyusun laporan", nameof(PatternInstructions.ReportTemplate))]
    public void Pattern_detection_texts_match_the_asd(string heading, string field)
    {
        var asd = File.ReadAllText(RepoPaths.Contracts("asd-pattern-detection.md")).Replace("\r\n", "\n");
        var expected = TextBlock().Match(asd[asd.IndexOf(heading, StringComparison.Ordinal)..]).Groups[1].Value;
        var text = (string)typeof(PatternInstructions).GetField(field)!.GetValue(null)!;

        Assert.Equal(expected, text.Replace("\r\n", "\n"));
    }

    [GeneratedRegex("```text\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex TextBlock();
}
