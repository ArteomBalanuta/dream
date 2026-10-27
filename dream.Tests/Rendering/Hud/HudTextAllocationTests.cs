using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.Hud;

public class HudTextAllocationTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cultures => ["", "de-DE", "en-US", "fr-FR"];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void TryFormatHudLine_MatchesToHudLines(string cultureName)
    {
        using CultureScope _ = new(cultureName);
        foreach (FramePerformanceSnapshot snapshot in CreateSnapshots())
        {
            string[] expected = snapshot.ToHudLines().ToArray();
            Assert.Equal(FramePerformanceSnapshot.HudLineCount, expected.Length);

            byte[] buffer = new byte[2048];
            for (int index = 0; index < FramePerformanceSnapshot.HudLineCount; index++)
            {
                Assert.True(snapshot.TryFormatHudLine(index, buffer, out int written));
                Assert.Equal(expected[index], Encoding.UTF8.GetString(buffer, 0, written));
                Assert.Equal(expected[index], snapshot.FormatHudLine(index));
            }
        }
    }

    [Fact]
    public void TryFormatHudLine_UsesCommaDecimalsUnderGermanCulture()
    {
        using CultureScope _ = new("de-DE");
        FramePerformanceSnapshot snapshot = new(1.25, 0.75, 0, 0, 0, 0, 0, 0, 0);

        Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
        Assert.True(snapshot.TryFormatHudLine(1, buffer, out int written));

        Assert.Equal("Export: 1,25 ms", Encoding.UTF8.GetString(buffer[..written]));
    }

    [Fact]
    public void TryFormatHudLine_ReturnsFalseWhenTheBufferIsTooSmall()
    {
        FramePerformanceSnapshot snapshot = CreateSnapshots()[0];

        Span<byte> buffer = stackalloc byte[8];

        Assert.False(snapshot.TryFormatHudLine(0, buffer, out _));
    }

    [Fact]
    public void TryFormatHudLine_AllocatesNothing()
    {
        FramePerformanceSnapshot snapshot = CreateSnapshots()[0];
        long before = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            foreach (string line in snapshot.ToHudLines())
            {
                GC.KeepAlive(line);
            }
        });

        long after = AllocationAssert.AtMost(0, () =>
        {
            Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
            for (int index = 0; index < FramePerformanceSnapshot.HudLineCount; index++)
            {
                if (!snapshot.TryFormatHudLine(index, buffer, out _))
                {
                    throw new InvalidOperationException("HUD line did not fit the stack buffer.");
                }
            }
        });

        output.WriteLine($"Performance HUD lines: ToHudLines {before} B/frame, TryFormatHudLine {after} B/frame");
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void HudTextHandler_MatchesStringInterpolation(string cultureName)
    {
        using CultureScope _ = new(cultureName);
        int width = 1920;
        int height = 1080;
        double milliseconds = 1234.5678;
        string? connection = "127.0.0.1:7777 (ü)";
        string? missing = null;

        Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];

        Assert.Equal($"Resolution: {width} x {height}", Capture(buffer, $"Resolution: {width} x {height}"));
        Assert.Equal($"Server: {connection}", Capture(buffer, $"Server: {connection}"));
        Assert.Equal($"Missing: {missing}.", Capture(buffer, $"Missing: {missing}."));
        Assert.Equal($"Time {milliseconds:0.00} ms {milliseconds}", Capture(buffer, $"Time {milliseconds:0.00} ms {milliseconds}"));
        Assert.Equal($"Mode {PlayerInteractionMode.Combat}", Capture(buffer, $"Mode {PlayerInteractionMode.Combat}"));
    }

    [Fact]
    public void HudTextHandler_FallsBackToTheFullStringWhenTheBufferIsTooSmall()
    {
        using CultureScope _ = new("de-DE");
        string name = new('x', 40);
        double value = 9876.54321;

        Span<byte> buffer = stackalloc byte[16];

        Assert.False(TryCaptureUtf8(buffer, $"Name {name} value {value:0.00}"));
        Assert.Equal($"Name {name} value {value:0.00}", Capture(buffer, $"Name {name} value {value:0.00}"));
        Assert.Equal($"Literal only that is longer than sixteen bytes", Capture(buffer, $"Literal only that is longer than sixteen bytes"));
        Assert.Equal($"{value:0.00} then a long tail of text", Capture(buffer, $"{value:0.00} then a long tail of text"));
    }

    [Fact]
    public void HudTextHandler_AllocatesNothingWhenTheTextFits()
    {
        int width = 1920;
        int height = 1080;
        int players = 3;
        string connection = "127.0.0.1:7777";
        long before = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            GC.KeepAlive($"Server: {connection}");
            GC.KeepAlive($"Players: {players}");
            GC.KeepAlive($"Resolution: {width} x {height}");
        });

        long after = AllocationAssert.AtMost(0, () =>
        {
            Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
            if (!TryCaptureUtf8(buffer, $"Server: {connection}") ||
                !TryCaptureUtf8(buffer, $"Players: {players}") ||
                !TryCaptureUtf8(buffer, $"Resolution: {width} x {height}"))
            {
                throw new InvalidOperationException("HUD text did not fit the stack buffer.");
            }
        });

        output.WriteLine($"Render HUD strings: $\"\" {before} B/frame, HudText {after} B/frame");
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void HudStatusSnapshot_CachedLabelsMatchStringInterpolation(string cultureName)
    {
        using CultureScope _ = new(cultureName);
        HudStatusSnapshot[] snapshots =
        [
            new(82, 100, PlayerInteractionMode.Combat, 25, 50, 10, 2f, 10, 1, 14, 1023),
            new(81, 100, PlayerInteractionMode.Building, 25, 51, 10, 2f, 10, 2, 512, 1023),
            new(-5, 0, PlayerInteractionMode.Combat, 0, 0, 10, 2f, 10, 0, 0, 0),
            new(82, 100, PlayerInteractionMode.Combat, 25, 50, 10, 2f, 10, 1, 14, 1023)
        ];

        foreach (HudStatusSnapshot status in snapshots)
        {
            for (int read = 0; read < 2; read++)
            {
                Assert.Equal($"HP: {status.CurrentHealth}/{status.MaxHealth}", status.HealthLabel);
                Assert.Equal($"Mode: {status.InteractionMode.ToHudLabel()}", status.ModeLabel);
                Assert.Equal($"Damage: {status.MinDamage}-{status.MaxDamage}", status.DamageLabel);
                Assert.Equal($"LVL: {status.Level}", status.LevelLabel);
                Assert.Equal($"EXPERIENCE: {status.ExperiencePercent}%", status.ExperiencePercentLabel);
                Assert.Equal($"{status.Experience}/{status.ExperienceToNextLevel}", status.ExperienceProgressLabel);
            }
        }
    }

    [Fact]
    public void HudStatusSnapshot_UnchangedLabelsAllocateNothing()
    {
        HudStatusSnapshot status = new(82, 100, PlayerInteractionMode.Combat, 25, 50, 10, 2f, 10, 1, 14, 1023);

        long after = AllocationAssert.AtMost(0, () =>
        {
            GC.KeepAlive(status.HealthLabel);
            GC.KeepAlive(status.ModeLabel);
            GC.KeepAlive(status.DamageLabel);
            GC.KeepAlive(status.LevelLabel);
            GC.KeepAlive(status.ExperiencePercentLabel);
            GC.KeepAlive(status.ExperienceProgressLabel);
        });

        output.WriteLine($"HUD status labels (unchanged values): {after} B/frame");
    }

    private static FramePerformanceSnapshot[] CreateSnapshots()
    {
        return
        [
            new(
                RenderExportMilliseconds: 1.25,
                PhysicsStepMilliseconds: 0.75,
                ExportedCompatibilityCubes: 300,
                ExportedGeometryBlocks: 600,
                VisibleCompatibilityCubes: 120,
                VisibleGeometryBlocks: 240,
                CompatibilityBatchCount: 2,
                GeometryBatchCount: 3,
                CollisionBodyCount: 900,
                VisibleStaticChunks: 12,
                StaticChunkMeshCount: 34,
                StaticChunkExposedFaces: 123456,
                StaticChunkTriangles: 246912,
                StaticChunkSignatureMilliseconds: 0.9,
                StaticChunkExtractMilliseconds: 1.1,
                StaticChunkPlanMilliseconds: 2.2,
                StaticChunkSyncMilliseconds: 3.3,
                StaticChunkDrawMilliseconds: 4.4,
                StaticChunkRebuiltMeshes: 5,
                StaticChunkReusedMeshes: 6,
                TotalFrameMilliseconds: 16.6,
                EnemyDrawMilliseconds: 1.2,
                LocalPlayerDrawMilliseconds: 0.8,
                RemotePlayerDrawMilliseconds: 1.4,
                WorldDrawMilliseconds: 7.8,
                ViewModelDrawMilliseconds: 1.6,
                DebugDrawMilliseconds: 0.4,
                HudDrawMilliseconds: 2.1,
                SwapBuffersMilliseconds: 3.2,
                ServerTickMilliseconds: 4.3,
                ServerSnapshotMilliseconds: 5.4,
                ServerBroadcastMilliseconds: 6.5),
            FramePerformanceSnapshot.Empty,
            new(
                RenderExportMilliseconds: 12345.675,
                PhysicsStepMilliseconds: -0.004,
                ExportedCompatibilityCubes: int.MaxValue,
                ExportedGeometryBlocks: int.MinValue,
                VisibleCompatibilityCubes: -1,
                VisibleGeometryBlocks: 1000000,
                CompatibilityBatchCount: 0,
                GeometryBatchCount: 7,
                CollisionBodyCount: 1234567,
                TotalFrameMilliseconds: 0.005,
                WorldDrawMilliseconds: 999999.999,
                HudDrawMilliseconds: double.NaN,
                ServerTickMilliseconds: double.PositiveInfinity,
                ServerSnapshotMilliseconds: double.NegativeInfinity)
        ];
    }

    private static string Capture(
        Span<byte> buffer,
        [InterpolatedStringHandlerArgument(nameof(buffer))] ref HudTextInterpolatedStringHandler text)
    {
        return text.TryGetUtf8(out ReadOnlySpan<byte> utf8)
            ? Encoding.UTF8.GetString(utf8)
            : text.ToStringAndClear();
    }

    private static bool TryCaptureUtf8(
        Span<byte> buffer,
        [InterpolatedStringHandlerArgument(nameof(buffer))] ref HudTextInterpolatedStringHandler text)
    {
        return text.TryGetUtf8(out _);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string cultureName)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previous;
        }
    }
}
