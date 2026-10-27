using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// ImGui.Text, TextColored, TextWrapped and TextDisabled treat their text as a printf format, so a
// "%" in a server name, player name, session id or item text would be misread. Render.cs passes
// such text through ImGui.TextUnformatted or HudText instead; only constant literals without "%"
// may still use the format overloads.
public class RenderHudTextFormatTests
{
    private const string LiteralWithoutPercent = "\"[^\"%]*\"";

    [Fact]
    public void Render_Has_No_Interpolated_ImGui_Text_Calls()
    {
        string source = ReadRenderSource();

        Assert.DoesNotMatch(new Regex(@"ImGui\.Text(Colored|Wrapped|Disabled)?\(\s*\$"), source);
    }

    [Fact]
    public void Render_Format_Text_Calls_Take_Only_Constant_Literals()
    {
        string source = ReadRenderSource();
        List<string> offenders = [];

        foreach (Match match in Regex.Matches(source, @"ImGui\.Text(Wrapped|Disabled)?\((?<arg>[^;]*)\);"))
        {
            if (!Regex.IsMatch(match.Groups["arg"].Value, $"^{LiteralWithoutPercent}$"))
            {
                offenders.Add(match.Value);
            }
        }

        foreach (Match match in Regex.Matches(source, @"ImGui\.TextColored\((?<args>[^;]*)\);"))
        {
            if (!Regex.IsMatch(match.Groups["args"].Value, $@",\s*{LiteralWithoutPercent}$"))
            {
                offenders.Add(match.Value);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Connected_Server_And_Player_Session_Lines_Are_Unformatted()
    {
        string source = ReadRenderSource();

        Assert.Matches(new Regex(@"(HudText\.Text\(hudText,|ImGui\.TextUnformatted\()\s*\$""Connected Server: \{connectionLabel\}""\)"), source);
        Assert.Matches(new Regex(@"(HudText\.Text\(hudText,|ImGui\.TextUnformatted\()\s*\$""\{player\.DisplayName\} \[\{player\.SessionId\}\]""\)"), source);
    }

    [Fact]
    public void Status_Labels_Are_Unformatted()
    {
        string source = ReadRenderSource();
        string[] labels =
        [
            "HealthLabel",
            "ModeLabel",
            "DamageLabel",
            "LevelLabel",
            "ExperiencePercentLabel",
            "ExperienceProgressLabel"
        ];

        foreach (string label in labels)
        {
            Assert.Contains($"ImGui.TextUnformatted(status.{label});", source);
        }
    }

    private static string ReadRenderSource()
    {
        return File.ReadAllText(Path.Combine(FindRepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
