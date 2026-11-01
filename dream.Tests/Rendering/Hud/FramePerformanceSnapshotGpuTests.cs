using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Hud;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Hud;

// GPU-5: GPU section times from GpuFrameTimer and the RenderCounters totals, on one HUD line.
public class FramePerformanceSnapshotGpuTests(ITestOutputHelper output)
{
    private const int GpuLineIndex = 14;

    private static readonly FramePerformanceSnapshot Snapshot = FramePerformanceSnapshot.Empty with
    {
        GpuWorldMilliseconds = 4.25,
        GpuActorsMilliseconds = 1.5,
        GpuEffectsMilliseconds = 0.75,
        GpuHudMilliseconds = 0.375,
        GpuPrimitives = 987654,
        DrawCalls = 640,
        ProgramBinds = 37,
        UniformUploads = 1500
    };

    public static TheoryData<string> Cultures => ["", "de-DE", "en-US", "fr-FR"];

    [Fact]
    public void Gpu_Line_Is_The_Last_Hud_Line()
    {
        string[] lines = Snapshot.ToHudLines().ToArray();

        Assert.Equal(15, FramePerformanceSnapshot.HudLineCount);
        Assert.Equal(GpuLineIndex, lines.Length - 1);
        Assert.Equal(
            "GPU: world 4.25 ms, actors 1.50 ms, fx 0.75 ms, hud 0.38 ms, prims 987654, draws 640, programs 37, uniforms 1500",
            lines[GpuLineIndex]);
    }

    [Fact]
    public void HasCounters_Is_True_When_Only_A_Gpu_Field_Is_Set()
    {
        Assert.True((FramePerformanceSnapshot.Empty with { GpuWorldMilliseconds = 0.01 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { GpuActorsMilliseconds = 0.01 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { GpuHudMilliseconds = 0.01 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { GpuEffectsMilliseconds = 0.01 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { GpuPrimitives = 1 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { DrawCalls = 1 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { ProgramBinds = 1 }).HasCounters);
        Assert.True((FramePerformanceSnapshot.Empty with { UniformUploads = 1 }).HasCounters);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Gpu_Line_Formats_Identically_Through_Both_Templates(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            FramePerformanceSnapshot[] snapshots =
            [
                Snapshot,
                FramePerformanceSnapshot.Empty,
                FramePerformanceSnapshot.Empty with
                {
                    GpuWorldMilliseconds = 99999.995,
                    GpuActorsMilliseconds = double.NaN,
                    GpuEffectsMilliseconds = double.PositiveInfinity,
                    GpuHudMilliseconds = double.NegativeInfinity,
                    GpuPrimitives = long.MinValue,
                    DrawCalls = int.MaxValue,
                    ProgramBinds = -1,
                    UniformUploads = int.MinValue
                }
            ];

            byte[] buffer = new byte[HudText.StackBufferBytes];
            foreach (FramePerformanceSnapshot snapshot in snapshots)
            {
                string expected = snapshot.FormatHudLine(GpuLineIndex);
                Assert.Equal(
                    $"GPU: world {snapshot.GpuWorldMilliseconds:0.00} ms, actors {snapshot.GpuActorsMilliseconds:0.00} ms, fx {snapshot.GpuEffectsMilliseconds:0.00} ms, hud {snapshot.GpuHudMilliseconds:0.00} ms, prims {snapshot.GpuPrimitives}, draws {snapshot.DrawCalls}, programs {snapshot.ProgramBinds}, uniforms {snapshot.UniformUploads}",
                    expected);
                Assert.True(snapshot.TryFormatHudLine(GpuLineIndex, buffer, out int written));
                Assert.Equal(expected, Encoding.UTF8.GetString(buffer, 0, written));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Gpu_Line_Uses_Comma_Decimals_Under_German_Culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal(
                "GPU: world 4,25 ms, actors 1,50 ms, fx 0,75 ms, hud 0,38 ms, prims 987654, draws 640, programs 37, uniforms 1500",
                Snapshot.FormatHudLine(GpuLineIndex));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Gpu_Line_Allocates_Nothing_Through_The_Span_Path()
    {
        long before = AllocationAssert.MeasureBytesPerIteration(() => GC.KeepAlive(Snapshot.FormatHudLine(GpuLineIndex)));

        long after = AllocationAssert.AtMost(0, () =>
        {
            Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
            if (!Snapshot.TryFormatHudLine(GpuLineIndex, buffer, out _))
            {
                throw new InvalidOperationException("The GPU line did not fit the stack buffer.");
            }
        });

        output.WriteLine($"GPU HUD line: FormatHudLine {before} B/frame, TryFormatHudLine {after} B/frame");
    }
}
