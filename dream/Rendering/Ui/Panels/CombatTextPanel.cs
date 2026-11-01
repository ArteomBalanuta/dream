using Dream.Rendering.Hud;
using Dream.Simulation.Combat;

namespace Dream.Rendering.Ui.Panels;

// Floating damage numbers drawn from the digit atlas (dmg.0 to dmg.9, two-channel mask: R fill, G rim).
// Reads HudView.CombatText, whose states carry ScreenAnchor already projected by the renderer.
// Motion per number: it starts large, shrinks to a floor within 0.15 s, rises on an ease-out curve and
// fades over the last quarter of 1.4 s. The newest number is drawn last (on top), and within one target
// an older number is held at least 0.8 of its own height above the next newer one, so settled rows
// stay readable. Allocates nothing: the draw order and positions live on the stack.
public static class CombatTextPanel
{
    // More numbers than this in one frame are not drawn (the system pools 150, 12 per target).
    public const int MaxDrawn = 256;

    // The digit sprites are 48 x 64 texels with a monospaced advance of 44 texels.
    private const float AdvanceOverHeight = 44f / 64f;
    private const float PitchOverHeight = 0.8f;

    private static readonly UiColor RegularFill = new(0xFD, 0xB4, 0x42, 255);
    private static readonly UiColor RegularRim = new(0x4A, 0x18, 0x00, 255);
    private static readonly UiColor CriticalFill = new(0x0A, 0x9A, 0xFE, 255);
    private static readonly UiColor CriticalRim = new(0x00, 0x17, 0x50, 255);
    private static readonly UiColor IncomingFill = new(0xFF, 0x00, 0x00, 255);
    private static readonly UiColor IncomingRim = new(0x40, 0x00, 0x00, 255);

    public static void Build(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        IReadOnlyList<FloatingCombatTextRenderState> states = v.CombatText;
        int count = Math.Min(states.Count, MaxDrawn);
        if (count == 0 || !s.TryGet(UiSpriteNames.Digit(0), out _))
        {
            return;
        }

        // Indices by spawn order, oldest first (insertion sort: the list is short and nearly sorted).
        Span<int> order = stackalloc int[MaxDrawn];
        for (int i = 0; i < count; i++)
        {
            long spawn = states[i].SpawnOrder;
            int j = i;
            while (j > 0 && states[order[j - 1]].SpawnOrder > spawn)
            {
                order[j] = order[j - 1];
                j--;
            }

            order[j] = i;
        }

        float pixelScale = l.ViewportHeight / FloatingCombatTextMotion.ClassicReferenceHeight;

        // Newest to oldest: where each number's baseline sits after the pitch guard.
        Span<float> baseline = stackalloc float[MaxDrawn];
        for (int k = count - 1; k >= 0; k--)
        {
            FloatingCombatTextRenderState state = states[order[k]];
            float size = pixelScale * state.SizeFactor;
            float fill = FloatingCombatTextMotion.ClassicFillPixels(state.AgeSeconds, state.Kind) * size;
            float y = state.ScreenAnchor.Y - (FloatingCombatTextMotion.ClassicRisePixels(state.AgeSeconds) * size);
            for (int j = k + 1; j < count; j++)
            {
                if (states[order[j]].TargetId == state.TargetId)
                {
                    float limit = baseline[j] - (PitchOverHeight * fill);
                    if (y > limit)
                    {
                        y = limit;
                    }

                    break;
                }
            }

            baseline[k] = y;
        }

        // Oldest to newest: the newest is drawn last, on top.
        for (int k = 0; k < count; k++)
        {
            FloatingCombatTextRenderState state = states[order[k]];
            float alpha = FloatingCombatTextMotion.ClassicAlpha(state.AgeSeconds);
            if (alpha <= 0f)
            {
                continue;
            }

            float size = pixelScale * state.SizeFactor;
            float height = FloatingCombatTextMotion.ClassicFillPixels(state.AgeSeconds, state.Kind) * size;
            DrawNumber(c, s, state, height, baseline[k], pixelScale, (byte)MathF.Round(alpha * 255f));
        }
    }

    private static void DrawNumber(IUiCanvas c, UiSpriteTable s, in FloatingCombatTextRenderState state, float height, float baseline, float pixelScale, byte alpha)
    {
        string text = state.Text;
        int digits = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is >= '0' and <= '9')
            {
                digits++;
            }
        }

        if (digits == 0 || height < 1f)
        {
            return;
        }

        GetColors(state.Kind, out UiColor fill, out UiColor rim);
        fill = fill.WithAlpha(alpha);
        rim = rim.WithAlpha(alpha);

        float advance = height * AdvanceOverHeight;
        float width = height * 0.75f;
        float jitter = ((int)(((ulong)state.SpawnOrder * 2654435761UL >> 16) % 7UL) - 3) * FloatingCombatTextMotion.ClassicJitterPixels * pixelScale / 3f;
        float x = state.ScreenAnchor.X + jitter - (((digits - 1) * advance) + width) * 0.5f;
        float top = baseline - height;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is < '0' or > '9')
            {
                continue;
            }

            if (s.TryGet(UiSpriteNames.Digit(ch - '0'), out UiSprite sprite))
            {
                c.MaskGlyph(sprite, new UiRect(x, top, width, height), fill, rim);
            }

            x += advance;
        }
    }

    private static void GetColors(FloatingCombatTextKind kind, out UiColor fill, out UiColor rim)
    {
        switch (kind)
        {
            case FloatingCombatTextKind.Critical:
                fill = CriticalFill;
                rim = CriticalRim;
                break;
            case FloatingCombatTextKind.Incoming:
                fill = IncomingFill;
                rim = IncomingRim;
                break;
            default:
                fill = RegularFill;
                rim = RegularRim;
                break;
        }
    }
}
