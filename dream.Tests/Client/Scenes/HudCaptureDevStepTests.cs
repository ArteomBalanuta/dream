using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Xunit;

namespace Dream.Tests.Client.Scenes;

public sealed class HudCaptureDevStepTests
{
    [Fact]
    public void ScriptParse_KeepsDevAndTodTokens()
    {
        HudCaptureScript script = HudCaptureScript.Parse("out", "play,DEV:Clear;Spawn:Wolf:2:3,tod:21.5,tod:off,wait:5")!;

        Assert.Equal(["play", "dev:clear;spawn:wolf:2:3", "tod:21.5", "tod:off", "wait:5"], script.Steps);
        Assert.Equal(HudCaptureVerb.Dev, script.Specs[1].Verb);
        Assert.Equal(2, script.Specs[1].A);
        Assert.Equal(HudCaptureVerb.TimeOfDay, script.Specs[2].Verb);
        Assert.Equal(21 * 60 + 30, script.Specs[2].A);
        Assert.Equal(-1, script.Specs[3].A);
    }

    [Fact]
    public void NineSubcommands_AreDropped_EightAreKept()
    {
        string eight = "dev:" + string.Join(';', Enumerable.Repeat("heal", 8));
        string nine = "dev:" + string.Join(';', Enumerable.Repeat("heal", 9));

        Assert.Equal([eight], HudCaptureScript.Parse("out", $"{eight},{nine}")!.Steps.Where(s => s.StartsWith("dev:")));
        Assert.False(HudCaptureDevStep.TryBuildChatLine(nine, out _, out _));
    }

    [Fact]
    public void TryBuildChatLine_RoundTripsLowercase_ThroughTheSharedSplitter()
    {
        Assert.True(HudCaptureDevStep.TryBuildChatLine("dev:clear;spawn:wolf:2:3;tp:camp:north-east-wolves", out string line, out int n));

        Assert.Equal("/dev clear; spawn wolf 2 3; tp camp north-east-wolves", line);
        Assert.Equal(3, n);
        Assert.Equal(["clear", "spawn wolf 2 3", "tp camp north-east-wolves"], DevCommandText.SplitSubcommands(line));
    }

    [Theory]
    [InlineData("dev:")]
    [InlineData("dev:;")]
    [InlineData("dev:clear;;heal")]
    [InlineData("dev:clear;")]
    [InlineData("heal")]
    public void MalformedSteps_AreRejected(string step)
    {
        Assert.False(HudCaptureDevStep.TryBuildChatLine(step, out string line, out int n));
        Assert.Equal(0, n);
        Assert.Equal(string.Empty, line);
        Assert.False(HudCaptureStepSpec.TryParse(step, out _));
    }

    [Fact]
    public void TooLongLine_IsRejected_AtTheLimitAccepted()
    {
        static string Pad(int total) => "dev:give:" + new string('a', total - "/dev give ".Length);

        Assert.True(HudCaptureDevStep.TryBuildChatLine(Pad(ClientChatMessage.MaxTextLength), out string ok, out _));
        Assert.Equal(ClientChatMessage.MaxTextLength, ok.Length);
        Assert.False(HudCaptureDevStep.TryBuildChatLine(Pad(ClientChatMessage.MaxTextLength + 1), out _, out _));
    }

    [Theory]
    [InlineData(ChatChannel.System, "[dev] spawned 1 Wolf", true)]
    [InlineData(ChatChannel.System, "[dev] error: nope", true)]
    [InlineData(ChatChannel.World, "[dev] spawned 1 Wolf", false)]
    [InlineData(ChatChannel.Say, "[dev] x", false)]
    [InlineData(ChatChannel.System, "spawned 1 Wolf", false)]
    [InlineData(ChatChannel.System, "[DEV] x", false)]
    public void IsReply_NeedsSystemChannelAndPrefix(ChatChannel channel, string text, bool expected)
    {
        Assert.Equal(expected, HudCaptureDevStep.IsReply(channel, text));
    }

    [Fact]
    public void LogFormats_AreExact()
    {
        Assert.Equal("[hud-capture] dev /dev heal -> [dev] ok", HudCaptureDevStep.FormatReplyLog("/dev heal", "[dev] ok"));
        Assert.Equal("[hud-capture] dev /dev heal -> no reply (2 missing)", HudCaptureDevStep.FormatTimeoutLog("/dev heal", 2));
    }

    [Theory]
    [InlineData("tod:0", 0)]
    [InlineData("tod:12", 720)]
    [InlineData("tod:23.999", 1439)]
    [InlineData("tod:off", -1)]
    public void Tod_Parses(string step, int minutes)
    {
        Assert.True(HudCaptureStepSpec.TryParse(step, out HudCaptureStepSpec spec));
        Assert.Equal(HudCaptureVerb.TimeOfDay, spec.Verb);
        Assert.Equal(minutes, spec.A);
    }

    [Theory]
    [InlineData("tod:24")]
    [InlineData("tod:-1")]
    [InlineData("tod:abc")]
    [InlineData("tod:nan")]
    [InlineData("tod:")]
    [InlineData("tod:1:2")]
    public void Tod_RejectsOutOfRangeAndGarbage(string step)
    {
        Assert.False(HudCaptureStepSpec.TryParse(step, out _));
    }

    [Fact]
    public void CaptureScript_ParsesAsPowerShell_WithoutErrors()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "skills", "capturing-dream-screenshots", "scripts", "capture.ps1")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        string path = Path.Combine(dir!, "skills", "capturing-dream-screenshots", "scripts", "capture.ps1");
        var psi = new System.Diagnostics.ProcessStartInfo("powershell", ["-NoProfile", "-Command",
            "$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($env:CAPTURE_PS1,[ref]$null,[ref]$e);if($e){$e|Out-String;exit 1}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["CAPTURE_PS1"] = path;
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, output);
    }

    [Fact]
    public void WholeLineRefusals_ArePinnedToTheServersActualReplies()
    {
        Assert.Equal(Dream.Server.DedicatedServerHost.DevCommandsOffText, HudCaptureDevStep.WholeLineOffText);
        Assert.Equal(Dream.Server.DedicatedServerHost.DevUnavailableText, HudCaptureDevStep.WholeLineUnavailableText);
        Assert.Equal(Dream.Server.DedicatedServerHost.DevTooManyText, HudCaptureDevStep.WholeLineTooManyText);
        Assert.Contains(DevCommandText.MaxSubcommands.ToString(), HudCaptureDevStep.WholeLineTooManyText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChatChannel.System, "[dev] error: Dev commands are off on this server.", true)]
    [InlineData(ChatChannel.System, "[dev] error: Dev commands are not available.", true)]
    [InlineData(ChatChannel.System, "[dev] error: Too many subcommands (at most 8); nothing was run.", true)]
    [InlineData(ChatChannel.System, "[dev] error: Unknown item 'x'.", false)]
    [InlineData(ChatChannel.System, "[dev] gave 1 tool.torch", false)]
    [InlineData(ChatChannel.System, "[dev] error: Dev commands are off on this server. extra", false)]
    [InlineData(ChatChannel.Say, "[dev] error: Dev commands are off on this server.", false)]
    public void IsWholeLineRefusal_MatchesOnlyTheExactRefusals(ChatChannel channel, string text, bool expected)
    {
        Assert.Equal(expected, HudCaptureDevStep.IsWholeLineRefusal(channel, text));
    }
}
