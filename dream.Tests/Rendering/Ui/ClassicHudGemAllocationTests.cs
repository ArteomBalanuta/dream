using Dream.Assets;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Players;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui;

// Classic HUD iteration 2 verification: the plan's gate "0 B per frame on the surface test, with the gem
// animating". The health changes every frame, so the eased level, the surface phase and the glint all
// move, and the real UiBatch records the frame.
[Collection(TimingSensitiveCollection.Name)]
public sealed class ClassicHudGemAllocationTests : IDisposable
{
    private sealed class Backend(UiSpriteTable table, UiBatch batch) : IClassicHudBackend
    {
        public ClassicHudBackendState State => ClassicHudBackendState.Ready;

        public string? FailureReason => null;

        public UiSpriteTable? Sprites => table;

        public IUiCanvas BeginFrame(in UiLayout layout)
        {
            batch.BeginFrame(in layout);
            return batch;
        }

        public void Submit()
        {
        }

        public void Dispose()
        {
        }
    }

    public ClassicHudGemAllocationTests()
    {
        InventoryUiState.Reset();
        ClassicHud.Reset();
    }

    public void Dispose() => InventoryUiState.Reset();

    // A health change rebuilds the stat rows (HudViewBuilder.Refresh, CharacterPanelModel.BuildStatRows), which
    // allocates: 464 B measured. That is once per change, not per frame. The frames after it, while the gem
    // is still easing toward the new level, must allocate nothing.
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    public void WholeSurfaceFrame_WhileTheGemEases_AllocatesNothing_AndAHealthChangeCostsOnceOnItsOwnFrame(int width, int height)
    {
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        UiBatch batch = new(BlockGlyphs.Create());
        batch.SetSpriteTable(table!);
        ClassicHudSurface surface = new(new Backend(table!, batch));

        void Draw(int hp)
        {
            HudStatusSnapshot status = new(hp, 100, default, 1, 2, 3, 1f, 4, 1, 0, 50);
            ClassicHudFrame f = new()
            {
                Width = width,
                Height = height,
                ConfiguredScale = 0f,
                Pointer = new UiPointer(new Vector2(5f, 5f), false, false, true),
                Status = status,
                InventoryOpen = false,
            };
            Assert.True(surface.Draw(in f));
            surface.TakeRequests(out _, out _);
        }

        // Warm-up long enough for the runtime's tiered compilation (delay 100 ms, then the PGO tier) to settle.
        System.Diagnostics.Stopwatch warm = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; warm.ElapsedMilliseconds < 1500 || i < 400; i++)
        {
            Draw(100 - (i % 2));
        }

        // The measured health values take branches that 100 and 99 never reach (the first low-health frame
        // allocates once, so a measurement that started there read 264 B only when no earlier test in the
        // process had touched it). Draw each value before measuring, ending on the high one so the first
        // measured change is still a change.
        foreach (int hp in new[] { 30, 90 })
        {
            for (int i = 0; i < 400; i++)
            {
                Draw(hp);
            }
        }

        long worst = 0;
        long changeCost = 0;
        for (int round = 0; round < 20; round++)
        {
            int hp = round % 2 == 0 ? 30 : 90;
            long before = GC.GetAllocatedBytesForCurrentThread();
            Draw(hp);
            changeCost = Math.Max(changeCost, GC.GetAllocatedBytesForCurrentThread() - before);
            for (int i = 0; i < 20; i++)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                Draw(hp);
                worst = Math.Max(worst, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        Assert.True(batch.QuadCount > 0);
        Assert.Equal(0, worst);
        Assert.InRange(changeCost, 1, 1024);
    }
}
