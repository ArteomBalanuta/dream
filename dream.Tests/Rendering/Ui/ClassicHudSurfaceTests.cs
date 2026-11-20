using OpenTK.Mathematics;
using Xunit;
using Dream.Assets;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Simulation.Players;

namespace Dream.Tests.Rendering.Ui;

public class ClassicHudSurfaceTests
{
    private sealed class NullCanvas : IUiCanvas
    {
        public void Sprite(in UiSprite s, UiRect dst, UiColor tint) { }
        public void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint) { }
        public void NineSlice(in UiSprite s, UiRect dst, UiColor tint) { }
        public void Tile(in UiSprite s, UiRect dst, UiColor tint) { }
        public void Solid(UiRect dst, UiColor color) { }
        public void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim) { }
        public float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color) => 0f;
        public float MeasureText(ReadOnlySpan<char> text, UiTextStyle style) => 0f;
        public void PushClip(UiRect r) { }
        public void PopClip() { }
    }

    private sealed class FakeBackend : IClassicHudBackend
    {
        public ClassicHudBackendState State { get; set; } = ClassicHudBackendState.Ready;
        public string? FailureReason { get; set; }
        public UiSpriteTable? Sprites { get; set; }
        public int Begins { get; private set; }
        public int Submits { get; private set; }
        public int Width { get; private set; }
        public bool Disposed { get; private set; }
        public IUiCanvas BeginFrame(in UiLayout layout) { Begins++; Width = layout.ViewportWidth; return new NullCanvas(); }
        public void Submit() => Submits++;
        public void Dispose() => Disposed = true;
    }

    private static UiSpriteTable Sprites()
    {
        Assert.True(UiSpriteTable.TryLoad(AssetPaths.UiDirectory, out UiSpriteTable? table, out string error), error);
        return table!;
    }

    private static ClassicHudFrame Frame() => new()
    {
        Width = 1920,
        Height = 1080,
        ConfiguredScale = 0f,
        Pointer = new UiPointer(Vector2.Zero, false, false, false)
    };

    [Fact]
    public void Draw_WhileLoading_DrawsNothingAndTouchesNoCanvas()
    {
        FakeBackend backend = new() { State = ClassicHudBackendState.Loading };
        ClassicHudSurface surface = new(backend);

        Assert.False(surface.Draw(Frame()));
        Assert.Equal(0, backend.Begins);
        Assert.Equal(0, backend.Submits);
    }

    [Fact]
    public void Draw_WhenFailed_DrawsNothing()
    {
        FakeBackend backend = new() { State = ClassicHudBackendState.Failed, FailureReason = "bad" };
        ClassicHudSurface surface = new(backend);

        Assert.False(surface.Draw(Frame()));
        Assert.Equal(0, backend.Begins);
    }

    [Fact]
    public void Draw_WhenReady_BuildsThenSubmitsOnce_WithTheViewport()
    {
        FakeBackend backend = new() { Sprites = Sprites() };
        ClassicHudSurface surface = new(backend);

        Assert.True(surface.Draw(Frame()));

        Assert.Equal(1, backend.Begins);
        Assert.Equal(1, backend.Submits);
        Assert.Equal(1920, backend.Width);
    }

    [Fact]
    public void Draw_FillsTheViewFromTheFrame()
    {
        FakeBackend backend = new() { Sprites = Sprites() };
        ClassicHudSurface surface = new(backend);
        HudWorldLabel[] labels = [new HudWorldLabel(new Vector2(1, 2), "wolf", "10/10")];
        ClassicHudFrame frame = Frame() with { };

        surface.Draw(new ClassicHudFrame
        {
            Width = 1280,
            Height = 720,
            Pointer = frame.Pointer,
            InventoryOpen = true,
            PlayerPosition = new Vector3(1, 2, 3),
            RejectionMessage = "no",
            Labels = labels
        });

        Assert.True(surface.View.InventoryOpen);
        Assert.Equal(new Vector3(1, 2, 3), surface.View.PlayerPosition);
        Assert.Equal("no", surface.View.RejectionMessage);
        Assert.Same(labels, surface.View.Labels);
        Assert.Empty(surface.View.CombatText);
    }

    [Fact]
    public void Actions_AreCollectedOnceAndCleared()
    {
        FakeBackend backend = new() { Sprites = Sprites() };
        ClassicHudSurface surface = new(backend);
        IHudActions actions = surface;
        InventoryHudAction move = new(InventoryHudActionKind.Move, 1, 2);

        actions.Inventory(move);
        actions.CloseInventory();
        surface.TakeRequests(out InventoryHudAction? action, out bool close);
        surface.TakeRequests(out InventoryHudAction? again, out bool closeAgain);

        Assert.Equal(move, action);
        Assert.True(close);
        Assert.Null(again);
        Assert.False(closeAgain);
    }

    [Fact]
    public void Dispose_DisposesTheBackend()
    {
        FakeBackend backend = new();
        new ClassicHudSurface(backend).Dispose();

        Assert.True(backend.Disposed);
    }

    // Closing the inventory with a button still held must not reach gameplay as a fresh press:
    // GameplayInputResumeFilter swallows it until the button is released. Unchanged by the HUD.
    [Fact]
    public void CloseWithTheButtonHeld_StillSwallowsTheUseUntilReleased()
    {
        GameplayInputResumeFilter filter = new();
        PlayerCommandFrame Held(ulong seq, bool use) => new(
            new PlayerSessionId("s"), seq, PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(0f, 0f, 0f, false, false, false),
            new PlayerLookIntent(0f, 0f),
            PrimaryUsePressed: use);

        _ = filter.Filter(Held(1, false), allowGameplayInput: false);
        PlayerCommandFrame firstAfterClose = filter.Filter(Held(2, true), allowGameplayInput: true);
        PlayerCommandFrame stillHeld = filter.Filter(Held(3, true), allowGameplayInput: true);
        _ = filter.Filter(Held(4, false), allowGameplayInput: true);
        PlayerCommandFrame fresh = filter.Filter(Held(5, true), allowGameplayInput: true);

        Assert.False(firstAfterClose.PrimaryUsePressed);
        Assert.False(stillHeld.PrimaryUsePressed);
        Assert.True(fresh.PrimaryUsePressed);
    }

    // The world hover takes a hovered clickable label as its target. A frame the classic HUD does not build (still
    // loading, failed) must not leave the last frame's label hover behind, or the hover stays pinned on that NPC.
    [Fact]
    public void ALabelHover_DoesNotSurviveAFrameThatBuildsNoLabels()
    {
        FakeBackend backend = new() { Sprites = Sprites() };
        ClassicHudSurface surface = new(backend);
        HudLabelClick npc = new(HudLabelClickKind.InteractNpc, 5001);
        HudWorldLabel[] labels = [new HudWorldLabel(new Vector2(700f, 300f), "Mara", "", npc)];
        ClassicHudFrame frame = Frame() with { Pointer = new UiPointer(new Vector2(700f, 305f), false, false, true), Labels = labels };

        surface.Draw(frame);
        surface.Draw(frame);
        Assert.Equal(npc, surface.HoveredLabel);

        backend.State = ClassicHudBackendState.Loading;
        Assert.False(surface.Draw(frame));
        Assert.Equal(default, surface.HoveredLabel);
    }

    // And the renderer only reads the label hover from a frame the classic HUD really built: every path through
    // the HUD pass (built or not, a throw, the ImGui surface) sets the flag it is gated on.
    [Fact]
    public void TheRenderer_GatesTheLabelHover_OnEveryPathOfTheHudPass()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "dream", "Rendering")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string hud = File.ReadAllText(Path.Combine(root, "dream", "Rendering", "Core", "Render.Hud.cs"));
        Assert.Contains("ClassicHoveredLabel => _classicLabelsBuilt ?", hud, StringComparison.Ordinal);
        Assert.Contains("_classicLabelsBuilt = drew;", hud, StringComparison.Ordinal);
        int catchAt = hud.IndexOf("catch (Exception ex)", hud.IndexOf("_classicLabelsBuilt = drew;", StringComparison.Ordinal), StringComparison.Ordinal);
        int clearedInCatch = hud.IndexOf("_classicLabelsBuilt = false;", catchAt, StringComparison.Ordinal);
        Assert.True(catchAt > 0 && clearedInCatch > catchAt && clearedInCatch - catchAt < 200, "the catch must clear the flag");
        Assert.True(hud.IndexOf("_classicLabelsBuilt = false;", clearedInCatch + 1, StringComparison.Ordinal) > clearedInCatch, "the ImGui branch must clear the flag");
    }
}
