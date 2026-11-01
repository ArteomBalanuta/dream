using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// The seam between the GL-free surface and the foundation workstream's GL layer (atlas store,
// UiBatch, submit). Render.CreateClassicHudBackend returns the one implementation.
public interface IClassicHudBackend : IDisposable
{
    ClassicHudBackendState State { get; }

    // One line for the fault log when State is Failed.
    string? FailureReason { get; }

    // Valid when State is Ready.
    UiSpriteTable? Sprites { get; }

    // Starts a frame and returns the canvas the panels draw into. CPU only, no GL calls.
    IUiCanvas BeginFrame(int viewportWidth, int viewportHeight);

    // Uploads and draws what the canvas collected, then restores the GL state it changed.
    void Submit();
}

// What the surface needs from the renderer for one frame. A plain readonly struct, passed by in.
public readonly struct ClassicHudFrame
{
    public int Width { get; init; }

    public int Height { get; init; }

    // GameSettings.UiScaleSetting: 0 is auto.
    public float ConfiguredScale { get; init; }

    public UiPointer Pointer { get; init; }

    public HudStatusSnapshot Status { get; init; }

    public PlayerInventorySnapshot? Inventory { get; init; }

    public bool InventoryOpen { get; init; }

    public Vector3 PlayerPosition { get; init; }

    public string? RejectionMessage { get; init; }

    public WorldTransferHudState WorldTransfer { get; init; }

    public LootFeedbackSystem? Loot { get; init; }

    public IReadOnlyList<FloatingCombatTextRenderState>? CombatText { get; init; }

    public IReadOnlyList<Vector2>? CombatTextScreen { get; init; }

    public IReadOnlyList<HudWorldLabel>? Labels { get; init; }
}

// Builds and submits one classic HUD frame: fills the view, runs the panels against the backend's
// canvas and collects what the player picked. GL-free; the backend does all the GL. Panels send
// their choices here through IHudActions; the renderer turns them into a HudInteractionRequest.
public sealed class ClassicHudSurface : IHudActions, IDisposable
{
    private readonly IClassicHudBackend _backend;
    private readonly HudView _view = new();
    private HudViewBuilder? _builder;
    private InventoryHudAction? _action;
    private bool _closeInventory;

    public ClassicHudSurface(IClassicHudBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public IClassicHudBackend Backend => _backend;

    public UiInputRouter Input { get; } = new();

    public HudView View => _view;

    public ClassicHudBackendState State => _backend.State;

    // False until the atlas is ready, so a loading frame draws nothing.
    public bool Draw(in ClassicHudFrame frame)
    {
        UiSpriteTable? sprites = _backend.Sprites;
        if (_backend.State != ClassicHudBackendState.Ready || sprites is null)
        {
            return false;
        }

        _builder ??= new HudViewBuilder(sprites, _view);
        if (_builder.Refresh(frame.Status, frame.Inventory))
        {
            // The snapshot changed under a drag: the dragged slot may be a different item now.
            Input.CancelDrag();
        }

        HudView v = _view;
        v.PlayerPosition = frame.PlayerPosition;
        v.RejectionMessage = frame.RejectionMessage;
        v.WorldTransfer = frame.WorldTransfer;
        v.Loot = frame.Loot;
        v.InventoryOpen = frame.InventoryOpen;
        v.CombatText = frame.CombatText ?? Array.Empty<FloatingCombatTextRenderState>();
        v.CombatTextScreen = frame.CombatTextScreen ?? Array.Empty<Vector2>();
        v.Labels = frame.Labels ?? Array.Empty<HudWorldLabel>();

        UiLayout layout = UiLayout.Create(frame.Width, frame.Height, frame.ConfiguredScale);
        Input.BeginFrame(frame.Pointer);
        IUiCanvas canvas = _backend.BeginFrame(frame.Width, frame.Height);
        ClassicHud.Build(canvas, Input, v, in layout, sprites, this);
        Input.EndFrame();
        _backend.Submit();
        return true;
    }

    // What the panels picked this frame, cleared by the read.
    public void TakeRequests(out InventoryHudAction? action, out bool closeInventory)
    {
        action = _action;
        closeInventory = _closeInventory;
        _action = null;
        _closeInventory = false;
    }

    void IHudActions.Inventory(InventoryHudAction action) => _action = action;

    void IHudActions.CloseInventory() => _closeInventory = true;

    public void Dispose() => _backend.Dispose();
}
