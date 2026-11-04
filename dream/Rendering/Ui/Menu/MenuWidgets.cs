using Dream.Rendering.Ui.Panels;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Menu;

// What one slider did this frame.
public readonly record struct MenuSliderResult(bool Changed, float Unit, bool Committed);

// The few widgets of the front end (backdrop, panel, title, button, slider, text) over an IUiCanvas and
// an IUiInput. GL-free. One instance lives as long as its renderer: it keeps the slider drag between
// frames. Positions are in design units (640 x 480, 1 u = layout scale px), centred in the window.
public sealed class MenuWidgets
{
    public const float DesignHeightUnits = 480f;
    public const float ButtonWidthUnits = 150f;
    public const float ButtonHeightUnits = 20f;
    public const float ButtonPitchUnits = 26f;

    // Type sizes in design units: the menu plates are fixed art, so their text is sized from the box
    // it sits in rather than drawn at a fixed bake that shrinks against the plate as ui_scale rises.
    // A 13 u label fills the 20 u button plate the way the 13 px bake filled it at scale 1.
    public const float ButtonLabelUnits = 13f;
    public const float PanelTitleUnits = 18f;
    public const float OptionRowUnits = 11f;
    // The status and credit lines were on the pixel face, which reads as a terminal font in the
    // middle of engraved stone -- a red monospace "Session ended: the host closed the world" under
    // Cinzel buttons. They take the same face as the rest of the menu.
    public const float StatusUnits = 11f;

    public static readonly UiColor Bone = new(0xE2, 0xD6, 0xBC, 255);
    public static readonly UiColor Gold = new(0xF0, 0xC4, 0x6C, 255);
    public static readonly UiColor Muted = new(0x9C, 0x93, 0x82, 255);
    public static readonly UiColor Disabled = new(0x6A, 0x65, 0x5C, 255);
    public static readonly UiColor Error = new(0xE0, 0x6A, 0x50, 255);
    private static readonly UiColor Shadow = new(0, 0, 0, 210);

    private const int NoId = int.MinValue;

    private IUiCanvas? _canvas;
    private IUiInput? _input;
    private MenuSkin? _skin;
    private UiLayout _layout;
    private int _activeSlider = NoId;
    private int _releasedSlider = NoId;
    private bool _prevLeft;
    private bool _leftDown;
    private bool _pressedEdge;

    public IUiCanvas Canvas => _canvas ?? throw new InvalidOperationException("MenuWidgets.Begin has not run.");

    public IUiInput Input => _input ?? throw new InvalidOperationException("MenuWidgets.Begin has not run.");

    public MenuSkin Skin => _skin ?? throw new InvalidOperationException("MenuWidgets.Begin has not run.");

    public UiLayout Layout => _layout;

    public float Scale => _layout.Scale;

    public float ViewportWidth => _layout.ViewportWidth;

    public float ViewportHeight => _layout.ViewportHeight;

    // Top of the 480 u design area: the area is centred vertically when the window is taller than it.
    public float OriginY => MathF.Max(0f, (_layout.ViewportHeight - DesignHeightUnits * _layout.Scale) * 0.5f);

    public float CentreX => _layout.ViewportWidth * 0.5f;

    public void Begin(IUiCanvas canvas, IUiInput input, MenuSkin skin, in UiLayout layout)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _skin = skin ?? throw new ArgumentNullException(nameof(skin));
        _layout = layout;

        UiPointer pointer = input.Pointer;
        bool left = pointer.Interactive && pointer.LeftDown;
        _pressedEdge = left && !_prevLeft;
        bool releasedEdge = !left && _prevLeft;
        _prevLeft = left;
        _leftDown = left;
        _releasedSlider = releasedEdge ? _activeSlider : NoId;
        if (!left)
        {
            _activeSlider = NoId;
        }
    }

    // ---- geometry ----

    // A rect centred horizontally, y in design units from the top of the design area.
    public UiRect Centered(float yUnits, float wUnits, float hUnits)
    {
        float s = _layout.Scale;
        return new UiRect(MathF.Round(CentreX - wUnits * s * 0.5f), MathF.Round(OriginY + yUnits * s), wUnits * s, hUnits * s);
    }

    // A rect given in units relative to the top-left of a parent rect.
    public UiRect Inside(UiRect parent, float xUnits, float yUnits, float wUnits, float hUnits)
    {
        float s = _layout.Scale;
        return new UiRect(parent.X + xUnits * s, parent.Y + yUnits * s, wUnits * s, hUnits * s);
    }

    public UiRect ButtonRect(float topUnits, int index) =>
        Centered(topUnits + index * ButtonPitchUnits, ButtonWidthUnits, ButtonHeightUnits);

    // ---- text ----

    // Text about units tall for this scale: the engraved face when it fits maxWidthPx, else the pixel
    // face, which is squeezed if even that is too wide (the menu columns are fixed in units, so a long
    // label must give way rather than run into the next column).
    public UiSizedText FitText(ReadOnlySpan<char> text, float maxWidthPx, float units, bool allowTitle = true)
    {
        if (allowTitle)
        {
            UiSizedText title = UiText.For(in _layout, units, UiTextFace.Title);
            if (UiText.Measure(Canvas, text, in title) <= maxWidthPx)
            {
                return title;
            }
        }

        UiSizedText pixel = UiText.For(in _layout, units, UiTextFace.Pixel);
        float width = UiText.Measure(Canvas, text, in pixel);
        if (width > maxWidthPx && width > 0f && maxWidthPx > 0f)
        {
            float k = maxWidthPx / width;
            pixel = new UiSizedText(pixel.Style, pixel.Scale * k, pixel.Px * k);
        }

        return pixel;
    }

    // The same, but never taller than a box the art fixed: hUnits is the box in design units.
    public UiSizedText FitTextInBox(ReadOnlySpan<char> text, float maxWidthPx, float units, float hUnits, bool allowTitle = true)
    {
        UiSizedText t = FitText(text, maxWidthPx, MathF.Min(units, hUnits), allowTitle);
        float cap = hUnits * _layout.Scale;
        return t.Px <= cap || cap <= 0f
            ? t
            : new UiSizedText(t.Style, t.Scale * (cap / t.Px), cap);
    }

    // The line box the text occupies: the height a box has to fit it into.
    public float TextHeight(in UiSizedText t) => t.Px;

    // Draws one line with a dark shadow, centred on centreX, top at y.
    public void TextCentred(ReadOnlySpan<char> text, float centreX, float y, in UiSizedText t, UiColor color)
    {
        float w = UiText.Measure(Canvas, text, in t);
        TextAt(text, MathF.Round(centreX - w * 0.5f), y, in t, color);
    }

    public void TextAt(ReadOnlySpan<char> text, float x, float y, in UiSizedText t, UiColor color)
    {
        float off = UiText.ShadowPx(in _layout);
        float previous = Canvas.TextScale;
        Canvas.SetTextScale(t.Scale);
        Canvas.Text(text, new Vector2(x + off, MathF.Round(y) + off), t.Style, Shadow);
        Canvas.Text(text, new Vector2(x, MathF.Round(y)), t.Style, color);
        Canvas.SetTextScale(previous);
    }

    // Text vertically centred in a rect, left-aligned at x.
    public void TextLeftCentred(ReadOnlySpan<char> text, UiRect r, float x, in UiSizedText t, UiColor color) =>
        TextAt(text, x, r.Y + (r.H - t.Px) * 0.5f, in t, color);

    // ---- chrome ----

    // The backdrop covers the window (cropped to its aspect), or flat black when the art is missing.
    public void DrawBackdrop()
    {
        UiRect dst = new(0f, 0f, _layout.ViewportWidth, _layout.ViewportHeight);
        if (Skin.BackdropSprite is not { } sprite || sprite.W <= 0 || sprite.H <= 0)
        {
            Canvas.Solid(dst, new UiColor(6, 7, 9, 255));
            return;
        }

        float spriteAspect = (float)sprite.W / sprite.H;
        float viewAspect = dst.W / MathF.Max(dst.H, 1f);
        UiRect src = new(0f, 0f, 1f, 1f);
        if (viewAspect > spriteAspect)
        {
            float h = spriteAspect / viewAspect;
            src = new UiRect(0f, (1f - h) * 0.5f, 1f, h);
        }
        else if (viewAspect < spriteAspect)
        {
            float wFrac = viewAspect / spriteAspect;
            src = new UiRect((1f - wFrac) * 0.5f, 0f, wFrac, 1f);
        }

        Canvas.SpriteClipped(in sprite, dst, src, UiColor.White);
    }

    public void Dim(byte alpha) =>
        Canvas.Solid(new UiRect(0f, 0f, _layout.ViewportWidth, _layout.ViewportHeight), new UiColor(0, 0, 0, alpha));

    public void DrawPanel(UiRect r)
    {
        if (Skin.PanelSprite is { } panel)
        {
            if (panel.HasSlice)
            {
                Canvas.NineSlice(in panel, r, UiColor.White);
            }
            else
            {
                Canvas.Sprite(in panel, r, UiColor.White);
            }

            return;
        }

        Canvas.Solid(r, new UiColor(14, 14, 16, 245));
        float b = MathF.Max(1f, MathF.Round(_layout.Scale));
        UiColor edge = new(70, 68, 64, 255);
        Canvas.Solid(new UiRect(r.X, r.Y, r.W, b), edge);
        Canvas.Solid(new UiRect(r.X, r.Bottom - b, r.W, b), edge);
        Canvas.Solid(new UiRect(r.X, r.Y, b, r.H), edge);
        Canvas.Solid(new UiRect(r.Right - b, r.Y, b, r.H), edge);
    }

    // The title sprite, centred, at most 1.5x its art size and 90% of the window wide. Without the art
    // the word is drawn as large text.
    public void DrawTitle(float topUnits)
    {
        if (Skin.TitleSprite is { } sprite)
        {
            float pxPerTexel = _layout.Scale / UiLayout.ArtTexelsPerUnit;
            float scale = MathF.Min(1.5f, _layout.ViewportWidth * 0.9f / (sprite.W * pxPerTexel));
            float w = sprite.W * pxPerTexel * scale;
            float h = sprite.H * pxPerTexel * scale;
            Canvas.Sprite(in sprite, new UiRect(MathF.Round(CentreX - w * 0.5f), MathF.Round(OriginY + topUnits * _layout.Scale), w, h), UiColor.White);
            return;
        }

        UiSizedText word = FitText("DREAM", _layout.ViewportWidth * 0.9f, PanelTitleUnits * 2f);
        TextCentred("DREAM", CentreX, OriginY + topUnits * _layout.Scale, in word, Gold);
    }

    public void DrawDivider(UiRect r)
    {
        if (Skin.DividerSprite is { } sprite)
        {
            Canvas.Sprite(in sprite, r, UiColor.White);
            return;
        }

        float t = MathF.Max(1f, MathF.Round(_layout.Scale * 0.5f));
        Canvas.Solid(new UiRect(r.X, r.Y + (r.H - t) * 0.5f, r.W, t), new UiColor(110, 84, 40, 200));
    }

    // ---- controls ----

    // A button; true on the frame it is clicked (on release). A disabled button is drawn dimmed and never clicks.
    public bool Button(int id, UiRect r, string label, bool enabled = true)
    {
        bool hovered = Input.Hovered(id, r) && enabled;
        bool clicked = Input.Button(id, r, enabled) && enabled;
        bool down = hovered && Input.Pointer.LeftDown;

        UiSprite? plate = !enabled ? Skin.ButtonDisabledSprite
            : down ? Skin.ButtonPressedSprite
            : hovered ? Skin.ButtonHoverSprite
            : Skin.ButtonNormalSprite;
        if (plate is { } sprite)
        {
            if (sprite.HasSlice)
            {
                Canvas.NineSlice(in sprite, r, UiColor.White);
            }
            else
            {
                Canvas.Sprite(in sprite, r, UiColor.White);
            }
        }
        else
        {
            Canvas.Solid(r, hovered ? new UiColor(60, 44, 30, 255) : new UiColor(26, 26, 28, 255));
        }

        // Fitted in both directions: the plate is fixed art, and with a continuous scale an engraved
        // label sized from the scale can outgrow the plate's height as easily as its width.
        float plateUnits = _layout.Scale > 0f ? r.H / _layout.Scale : ButtonHeightUnits;
        UiSizedText t = FitTextInBox(label, r.W * 0.92f, ButtonLabelUnits, plateUnits);
        UiColor color = !enabled ? Disabled : hovered ? Gold : Bone;
        float push = down ? UiText.ShadowPx(in _layout) : 0f;
        float w = UiText.Measure(Canvas, label, in t);
        float x = MathF.Round(r.X + (r.W - w) * 0.5f);
        float y = r.Y + (r.H - t.Px) * 0.5f + push;
        TextAt(label, x, y, in t, color);
        return clicked;
    }

    // A horizontal slider. track is where the groove is drawn, hit the (taller) pointer area. unit is 0..1.
    // While the left button is held after a press inside hit, the slider follows the pointer.
    public MenuSliderResult Slider(int id, UiRect track, UiRect hit, float unit, bool enabled = true)
    {
        unit = Math.Clamp(float.IsFinite(unit) ? unit : 0f, 0f, 1f);
        bool hovered = Input.Hovered(id, hit);
        UiPointer pointer = Input.Pointer;
        if (enabled && _pressedEdge && hovered && hit.Contains(pointer.Position))
        {
            _activeSlider = id;
        }

        bool changed = false;
        if (enabled && _activeSlider == id && _leftDown && float.IsFinite(pointer.Position.X) && track.W > 0f)
        {
            float u = Math.Clamp((pointer.Position.X - track.X) / track.W, 0f, 1f);
            changed = true;
            unit = u;
        }

        bool committed = enabled && _releasedSlider == id;
        DrawSlider(track, unit, enabled, hovered || _activeSlider == id);
        return new MenuSliderResult(changed, unit, committed);
    }

    private void DrawSlider(UiRect track, float unit, bool enabled, bool hot)
    {
        if (Skin.SliderTrackSprite is { } sprite)
        {
            if (sprite.HasSlice)
            {
                Canvas.NineSlice(in sprite, track, UiColor.White);
            }
            else
            {
                Canvas.Sprite(in sprite, track, UiColor.White);
            }
        }
        else
        {
            Canvas.Solid(track, new UiColor(10, 10, 12, 255));
        }

        float pad = _layout.Scale * 1.4f;
        UiRect groove = new(track.X + pad, track.Y + pad, MathF.Max(0f, track.W - 2f * pad), MathF.Max(0f, track.H - 2f * pad));
        if (groove.W > 0f)
        {
            UiColor fill = enabled ? new UiColor(168, 104, 38, 220) : new UiColor(80, 76, 70, 200);
            Canvas.Solid(new UiRect(groove.X, groove.Y, groove.W * unit, groove.H), fill);
        }

        float knobCentre = track.X + track.W * unit;
        if (Skin.SliderKnobSprite is { } knob)
        {
            float pxPerTexel = _layout.Scale / UiLayout.ArtTexelsPerUnit;
            float kw = knob.W * pxPerTexel;
            float kh = knob.H * pxPerTexel;
            UiColor tint = enabled ? (hot ? new UiColor(255, 232, 190, 255) : UiColor.White) : new UiColor(120, 120, 120, 255);
            Canvas.Sprite(in knob, new UiRect(MathF.Round(knobCentre - kw * 0.5f), MathF.Round(track.Y + (track.H - kh) * 0.5f), kw, kh), tint);
        }
        else
        {
            float k = track.H * 1.6f;
            Canvas.Solid(new UiRect(knobCentre - k * 0.25f, track.Y + (track.H - k) * 0.5f, k * 0.5f, k), hot ? Gold : Bone);
        }
    }
}
