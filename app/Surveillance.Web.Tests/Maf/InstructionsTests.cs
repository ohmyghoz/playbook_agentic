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

    [GeneratedRegex("```text\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex TextBlock();
}
