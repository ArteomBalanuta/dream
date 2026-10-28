using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Hud;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Hud;

// The time PlayScene.Render spends before the world draw (GL.Clear, presentation capture and the
// remote-player states). Before GFX-4 it was only appended to the render-perf log line.
public class FramePerformanceSnapshotPreDrawTests(ITestOutputHelper output)
{
    // The pre-draw line was last until GPU-5 appended the GPU line after it, so the index is fixed.
    private const int PreDrawLineIndex = 13;

    private static readonly FramePerformanceSnapshot Snapshot = new(
        RenderExportMilliseconds: 0,
        PhysicsStepMilliseconds: 0,
        ExportedCompatibilityCubes: 0,
        ExportedGeometryBlocks: 0,
        VisibleCompatibilityCubes: 0,
        VisibleGeometryBlocks: 0,
        CompatibilityBatchCount: 0,
        GeometryBatchCount: 0,
        CollisionBodyCount: 0,
        PreDrawMilliseconds: 1.375,
        PreDrawClearMilliseconds: 0.125,
        PreDrawCaptureMilliseconds: 1.0,
        PreDrawRemoteStatesMilliseconds: 0.25);

    public static TheoryData<string> Cultures => ["", "de-DE", "en-US", "fr-FR"];

    [Fact]
    public void ToHudLines_Includes_The_PreDraw_Line()
    {
        string[] lines = Snapshot.ToHudLines().ToArray();

        Assert.Equal(FramePerformanceSnapshot.HudLineCount, lines.Length);
        Assert.Equal("Pre-draw: 1.38 ms, clear 0.13 ms, capture 1.00 ms, remote states 0.25 ms", lines[PreDrawLineIndex]);
        Assert.Equal(lines[PreDrawLineIndex], Snapshot.FormatHudLine(PreDrawLineIndex));
    }

    [Fact]
    public void HasCounters_Is_True_When_Only_PreDraw_Is_Set()
    {
        Assert.True(Snapshot.HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { PreDrawClearMilliseconds = 0.01 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { PreDrawCaptureMilliseconds = 0.01 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { PreDrawRemoteStatesMilliseconds = 0.01 }).HasCounters);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void PreDraw_Line_Formats_Identically_Through_Both_Templates(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            FramePerformanceSnapshot[] snapshots =
            [
                Snapshot,
                FramePerformanceSnapshot.Empty,
                new(0, 0, 0, 0, 0, 0, 0, 0, 0,
                    PreDrawMilliseconds: 99999.995,
                    PreDrawClearMilliseconds: double.NaN,
                    PreDrawCaptureMilliseconds: double.PositiveInfinity,
                    PreDrawRemoteStatesMilliseconds: -0.004)
            ];

            byte[] buffer = new byte[HudText.StackBufferBytes];
            foreach (FramePerformanceSnapshot snapshot in snapshots)
            {
                string expected = snapshot.FormatHudLine(PreDrawLineIndex);
                Assert.StartsWith("Pre-draw: ", expected);
                Assert.Equal($"Pre-draw: {snapshot.PreDrawMilliseconds:0.00} ms, clear {snapshot.PreDrawClearMilliseconds:0.00} ms, capture {snapshot.PreDrawCaptureMilliseconds:0.00} ms, remote states {snapshot.PreDrawRemoteStatesMilliseconds:0.00} ms", expected);
                Assert.True(snapshot.TryFormatHudLine(PreDrawLineIndex, buffer, out int written));
                Assert.Equal(expected, Encoding.UTF8.GetString(buffer, 0, written));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void PreDraw_Line_Uses_Comma_Decimals_Under_German_Culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal("Pre-draw: 1,38 ms, clear 0,13 ms, capture 1,00 ms, remote states 0,25 ms", Snapshot.FormatHudLine(PreDrawLineIndex));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void PreDraw_Line_Allocates_Nothing_Through_The_Span_Path()
    {
        long before = AllocationAssert.MeasureBytesPerIteration(() => GC.KeepAlive(Snapshot.FormatHudLine(PreDrawLineIndex)));

        long after = AllocationAssert.AtMost(0, () =>
        {
            Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
            if (!Snapshot.TryFormatHudLine(PreDrawLineIndex, buffer, out _))
            {
                throw new InvalidOperationException("The pre-draw line did not fit the stack buffer.");
            }
        });

        output.WriteLine($"Pre-draw HUD line: FormatHudLine {before} B/frame, TryFormatHudLine {after} B/frame");
    }
}
