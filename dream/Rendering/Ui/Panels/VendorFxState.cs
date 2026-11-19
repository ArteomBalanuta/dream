using Dream.Rendering.Hud;

namespace Dream.Rendering.Ui.Panels;

// The shop's short-lived feedback, thread-static like InventoryUiState: the ember glow of the shop frame
// while a sell is hovering, and the flash on the bag cell a bought item lands in. The clock is
// HudView.AnimationSeconds, set once a frame by ClassicHudSurface, so nothing here reads a timer or
// allocates. AnimationSeconds wraps, so every elapsed time that comes out negative counts as expired.
public sealed class VendorFxState
{
    public const float FlashSeconds = 0.45f;
    public const float EmberRiseSeconds = 0.12f;

    // A buy that has gained no bag cell after this long is forgotten (refused, or the bag was full).
    private const float PendingSeconds = 3f;

    [ThreadStatic]
    private static VendorFxState? _current;

    public static VendorFxState Current => _current ??= new VendorFxState();

    public static void Reset() => _current = null;

    private bool _pending;
    private ulong _pendingOccupancy;
    private float _pendingAt;
    private float _flashStart;
    private float _emberClock = float.NaN;
    private float _emberTarget;

    // The bag anchor flashing, or -1.
    public int FlashAnchor { get; private set; } = -1;

    // 0..1, the eased glow of the shop frame.
    public float EmberLevel { get; private set; }

    // Item icons by stock line. Looking an icon up builds the "item.<kind>" name, a string allocation, so a
    // line's icon is resolved once for the kind string it shows and reused every frame after.
    private readonly string?[] _iconKinds = new string?[VendorHudState.MaxStock];
    private readonly UiSprite[] _icons = new UiSprite[VendorHudState.MaxStock];
    private readonly bool[] _hasIcon = new bool[VendorHudState.MaxStock];
    private UiSpriteTable? _iconTable;

    // The icon of stock line `line`, which sells `kind`. False when the art has none.
    internal bool TryIcon(int line, string? kind, UiSpriteTable table, out UiSprite icon)
    {
        if ((uint)line >= (uint)_icons.Length)
        {
            icon = default;
            return kind is not null && table.TryGet(UiSpriteNames.Item(kind), out icon);
        }

        if (!ReferenceEquals(_iconTable, table))
        {
            Array.Clear(_iconKinds);
            _iconTable = table;
        }

        if (!ReferenceEquals(_iconKinds[line], kind))
        {
            _hasIcon[line] = kind is not null && table.TryGet(UiSpriteNames.Item(kind), out _icons[line]);
            _iconKinds[line] = kind;
        }

        icon = _icons[line];
        return _hasIcon[line];
    }

    // Called once a frame before the bag is drawn. Resolves a pending buy into FlashAnchor: the first
    // frame the bag gains cells, the flash lands on the lowest newly occupied cell's item.
    internal void BeginFrame(HudView v)
    {
        float now = v.AnimationSeconds;
        if (FlashAnchor >= 0)
        {
            float t = now - _flashStart;
            if (t < 0f || t >= FlashSeconds)
            {
                FlashAnchor = -1;
            }
        }

        if (!_pending)
        {
            return;
        }

        ulong gained = v.BagOccupancy() & ~_pendingOccupancy;
        if (gained != 0)
        {
            int anchor = v.BagAnchorAt(System.Numerics.BitOperations.TrailingZeroCount(gained));
            _pending = false;
            if (anchor >= 0)
            {
                FlashAnchor = anchor;
                _flashStart = now;
            }

            return;
        }

        float waited = now - _pendingAt;
        if (waited < 0f || waited > PendingSeconds)
        {
            _pending = false;
        }
    }

    // A buy was sent: remember which cells were full, so the item that arrives can flash. The first cells
    // gained are taken to be the purchase (a pickup landing in the same window can be mistaken for it; the
    // flash is cosmetic and the window is short).
    internal void NoteBuy(HudView v)
    {
        _pending = true;
        _pendingOccupancy = v.BagOccupancy();
        _pendingAt = v.AnimationSeconds;
    }

    // Called once a frame by the vendor window with what releasing the drag here would do. Valid raises
    // the ember glow over EmberRiseSeconds; anything else lets it fall at the same rate.
    internal void SetSellHover(InventoryWindow.DropVerdict verdict, float animationSeconds)
    {
        _emberTarget = verdict == InventoryWindow.DropVerdict.Valid ? 1f : 0f;
        float dt = animationSeconds - _emberClock;
        _emberClock = animationSeconds;
        if (float.IsNaN(dt) || dt < 0f || dt > 0.25f)
        {
            // First frame, a wrapped clock or a long stall: snap rather than glow for the wrong time.
            dt = EmberRiseSeconds;
        }

        float step = dt / EmberRiseSeconds;
        EmberLevel = _emberTarget > EmberLevel
            ? MathF.Min(_emberTarget, EmberLevel + step)
            : MathF.Max(_emberTarget, EmberLevel - step);
    }

    // 1 -> 0 over FlashSeconds from the frame the flash began; 0 when nothing flashes.
    public float FlashLevel(float animationSeconds)
    {
        if (FlashAnchor < 0)
        {
            return 0f;
        }

        float t = animationSeconds - _flashStart;
        return t < 0f || t >= FlashSeconds ? 0f : 1f - (t / FlashSeconds);
    }
}
