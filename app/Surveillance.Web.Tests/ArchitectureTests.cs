using Surveillance.Web.Tests.Support;

namespace Surveillance.Web.Tests;

/// <summary>Controllers and views must not know which agent solution is running.</summary>
public class ArchitectureTests
{
    // Namespaces, packages and class names of the agent frameworks and adapters.
    private static readonly string[] Forbidden =
    [
        "Microsoft.Agents", "Microsoft.Extensions.AI", "OpenAI", "LangGraph", "LangChain",
        "Google.Adk", "Google.GenAI", "Gemini", "Surveillance.Web.Agents", "FakeAgentClient",
        "MafAgentClient", "LangGraphAgentClient", "AdkAgentClient", "HttpSseAgentClient",
    ];

    public static TheoryData<string> Folders => new() { "Controllers", "Views", Path.Combine("wwwroot", "js") };

    [Theory]
    [MemberData(nameof(Folders))]
    public void Folder_has_no_agent_framework_references(string folder)
    {
        var path = Path.Combine(RepoPaths.Root, "app", "Surveillance.Web", folder);
        var offenders =
            from file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            where file.EndsWith(".cs") || file.EndsWith(".cshtml") || file.EndsWith(".js")
            let text = File.ReadAllText(file)
            from word in Forbidden
            where text.Contains(word, StringComparison.OrdinalIgnoreCase)
            select $"{Path.GetRelativePath(RepoPaths.Root, file)}: {word}";

        Assert.Empty(offenders);
    }
}
