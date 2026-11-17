using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Xunit;

namespace Dream.Tests.BugHunt;

// Regression tests for the BH-RND bug-hunt packet. Only the findings that can be pinned WITHOUT a GL
// context live here: the upload-budget accounting (BH-RND-4), the abandoned UI decodes (BH-RND-5) and
// the floating-combat-text tuning guard (BH-APP-X). BH-RND-1, -2, -3 and -6 are pure GL state seams
// (scissor snapshot, actor tint reaching the alpha-mask program, GL_ARRAY_BUFFER binding) and cannot
// be observed without a live context, so they are deliberately not faked here.
public class RndRenderingTests
{
    private const string Source = "rnd-test-loader";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    // BH-RND-4: a throwing upload uploads nothing, so it must not be counted or charged to the frame's
    // budget -- otherwise one permanently broken asset starves every healthy model queued behind it.
    [Fact]
    public void PumpUploads_DoesNotCountOrChargeTheBudgetForAFailedUpload()
    {
        RenderFaultLog faults = new(_ => { });
        using FakeLoaderHarness harness = new(faults, uploadFails: key => key == "broken");

        harness.Loader.Request("broken");
        harness.Loader.Request("healthy");
        harness.WaitUploadable(2);

        // A budget of one: before the fix the failed upload ate it and "healthy" slipped a frame.
        int uploaded = harness.Loader.PumpUploads(1);

        Assert.Equal(1, uploaded);
        Assert.True(harness.Loader.TryGet("healthy", out _));
        Assert.True(harness.Loader.IsFailed("broken"));
        Assert.Equal(1, faults.GetFaultCount(Source));
    }

    // BH-RND-4, the simple half: with nothing else queued, a frame whose only upload throws reports
    // zero uploads. The loading screen reads that count as progress.
    [Fact]
    public void PumpUploads_ReturnsZeroWhenTheOnlyUploadThrows()
    {
        RenderFaultLog faults = new(_ => { });
        using FakeLoaderHarness harness = new(faults, uploadFails: _ => true);

        harness.Loader.Request("broken");
        harness.WaitUploadable(1);

        Assert.Equal(0, harness.Loader.PumpUploads(4));
        Assert.True(harness.Loader.IsFailed("broken"));
    }

    // BH-RND-5: Fail leaves the store for good, and nothing drains the page decodes afterwards. An
    // abandoned decode that throws must reach the render fault log, not the finalizer.
    [Fact]
    public void UiAtlasStore_ObservesDecodesAbandonedByFail()
    {
        RenderFaultLog faults = new(_ => { });
        using ManualResetEventSlim gate = new(initialState: false);
        BlockingAtlasLoader loader = new(gate, tableThrows: true);
        UiPage[] pages = [UiPage.Window, UiPage.Hud];
        UiAtlasStore store = new(loader, new NullUploader(), pages: pages, faultSink: faults);

        store.Start();
        PumpUntil(store, () => store.State == UiAtlasState.Failed);
        Assert.Equal(UiAtlasState.Failed, store.State);

        // The decodes were still running when the store gave up; now let them throw.
        gate.Set();
        WaitFor(() => faults.GetFaultCount("ui-atlas") >= pages.Length, "abandoned decode faults");
        Assert.Equal(pages.Length, faults.GetFaultCount("ui-atlas"));
    }

    // BH-RND-5, the teardown half: the same rule on Dispose, which is how the scene tears the HUD down.
    [Fact]
    public void UiAtlasStore_ObservesDecodesAbandonedByDispose()
    {
        RenderFaultLog faults = new(_ => { });
        using ManualResetEventSlim gate = new(initialState: false);
        BlockingAtlasLoader loader = new(gate, tableThrows: false);
        UiPage[] pages = [UiPage.Window, UiPage.Hud];
        UiAtlasStore store = new(loader, new NullUploader(), pages: pages, faultSink: faults);

        store.Start();
        loader.WaitUntilDecoding(pages.Length);
        store.Dispose();

        gate.Set();
        WaitFor(() => faults.GetFaultCount("ui-atlas") >= pages.Length, "abandoned decode faults");
        Assert.Equal(UiAtlasState.Failed, store.State);
    }

    // BH-APP-X: Math.Clamp passes NaN through unchanged, so the guard has to reject non-finite values.
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void FloatingCombatTextTuning_NonFiniteValuesFallBackToTheDefault(float bad)
    {
        FloatingCombatTextTuning clamped = new FloatingCombatTextTuning(bad, bad, bad).Clamp();

        Assert.Equal(FloatingCombatTextTuning.Default.SizeMultiplier, clamped.SizeMultiplier);
        Assert.Equal(FloatingCombatTextTuning.Default.MotionSpeedMultiplier, clamped.MotionSpeedMultiplier);
        Assert.Equal(FloatingCombatTextTuning.Default.CriticalDepthMultiplier, clamped.CriticalDepthMultiplier);
        Assert.True(float.IsFinite(clamped.SizeMultiplier));
    }

    // Finite values keep clamping exactly as before: the guard must not move the range.
    [Fact]
    public void FloatingCombatTextTuning_FiniteValuesStillClampToTheRange()
    {
        FloatingCombatTextTuning clamped = new FloatingCombatTextTuning(99f, -99f, 99f).Clamp();

        Assert.Equal(FloatingCombatTextTuning.MaxSizeMultiplier, clamped.SizeMultiplier);
        Assert.Equal(FloatingCombatTextTuning.MinMotionSpeedMultiplier, clamped.MotionSpeedMultiplier);
        Assert.Equal(FloatingCombatTextTuning.MaxCriticalDepthMultiplier, clamped.CriticalDepthMultiplier);
    }

    private static void PumpUntil(UiAtlasStore store, Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!done())
        {
            store.Update();
            Assert.True(DateTime.UtcNow < deadline, "the atlas store never settled");
            Thread.Sleep(1);
        }
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            Thread.Sleep(1);
        }
    }

    private sealed class NullUploader : IUiTextureUploader
    {
        public int Upload(UiPage page, DecodedTexture texture) => 1;

        public void Delete(int handle)
        {
        }
    }

    // Every page decode blocks on the gate and then throws, so the test controls exactly when an
    // abandoned decode faults.
    private sealed class BlockingAtlasLoader(ManualResetEventSlim gate, bool tableThrows) : IUiAtlasLoader
    {
        private int _decoding;

        public UiSpriteTable LoadTable()
        {
            if (tableThrows)
            {
                throw new InvalidOperationException("sprite table unavailable");
            }

            return new UiSpriteTable();
        }

        public DecodedTexture DecodePage(UiPage page)
        {
            Interlocked.Increment(ref _decoding);
            Assert.True(gate.Wait(Deadline), "decode gate timed out");
            throw new InvalidOperationException($"{page} image is truncated");
        }

        public void WaitUntilDecoding(int count) =>
            WaitFor(() => Volatile.Read(ref _decoding) >= count, $"{count} decodes to start");
    }

    private sealed class FakeData(string key)
    {
        public string Key { get; } = key;
    }

    private sealed class FakeResource(string key) : IDisposable
    {
        public string Key { get; } = key;

        public void Dispose()
        {
        }
    }

    private sealed class FakeLoaderHarness : IDisposable
    {
        public FakeLoaderHarness(RenderFaultLog faults, Func<string, bool> uploadFails)
        {
            Loader = new AsyncResourceLoader<FakeData, FakeResource>(
                key => new FakeData(key),
                data => uploadFails(data.Key)
                    ? throw new InvalidOperationException($"upload failed: {data.Key}")
                    : new FakeResource(data.Key),
                faults,
                Source);
        }

        public AsyncResourceLoader<FakeData, FakeResource> Loader { get; }

        public void WaitUploadable(int count) =>
            WaitFor(() => Loader.UploadableCount >= count, $"{count} finished decodes");

        public void Dispose() => Loader.Dispose();
    }
}
