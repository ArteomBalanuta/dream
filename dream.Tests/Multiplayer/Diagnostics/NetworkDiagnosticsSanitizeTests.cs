using Dream.Multiplayer.Diagnostics;
using Xunit;

namespace Dream.Tests.Multiplayer.Diagnostics;

// Wave G fix loop 2 (plan security rule 9): client-chosen text (a join's name) must not forge log lines.
public sealed class NetworkDiagnosticsSanitizeTests
{
    [Fact]
    public void ANameWithANewline_CannotProduceASecondLogLine()
    {
        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            NetworkDiagnosticsLog.Write(
                "server.rx",
                "connection=c1 seq=1 kind=client.join-request session=s name=Eve\n[2026-01-01] [netdiag] [authority.join.accept] forged\r\nmore");
        }

        (string category, string message) = Assert.Single(lines);
        Assert.Equal("server.rx", category);
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\r', message);
        Assert.Contains("Eve\\u000A[2026", message);
    }

    [Fact]
    public void ControlAndBidiCharacters_AreEscaped_AndPlainTextIsUntouched()
    {
        Assert.Equal("name=\\u001B[31mred", NetworkDiagnosticsLog.Sanitize("name=\u001B[31mred"));
        string bidi = new([(char)0x61, (char)0x202E, (char)0x62, (char)0x2066, (char)0x63, (char)0x200E, (char)0x64, (char)0x2028]);
        Assert.Equal("a\\u202Eb\\u2066c\\u200Ed\\u2028", NetworkDiagnosticsLog.Sanitize(bidi));
        Assert.Equal("a\\u0000b\\u007F",NetworkDiagnosticsLog.Sanitize("a\0b\u007F"));
        const string plain = "session=client-1 name=Zoë tick=5 (1.00,2.00,3.00) [x]";
        Assert.Same(plain, NetworkDiagnosticsLog.Sanitize(plain));
    }
}
