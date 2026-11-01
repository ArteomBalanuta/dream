using System.Globalization;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The bottom bar: left, centre and right groups on the three base sprites, the tiled filler between
// them, then the dragons last. Positions are the art's own layout in units (u), read from the
// generator: a rect given in bar units x is group-relative x minus the group's start (0, 199, 441).
// Real data only: HP gem fill, EXP trough and level, mode, coordinates, satchel. Everything the game
// has no system for (MP, the two gauges, the stone slabs, Q/W/E, banner and lens) is drawn as an
// empty art socket with no number, no key letter and no input.
public static class StatusBarPanel
{
    public const int SatchelButtonId = 0x5B01;

    private const float CentreStart = UiLayout.LeftGroupUnits;
    private const float RightStart = UiLayout.RightGroupStartUnits;

    private static readonly UiColor NumberColor = new(0xF8, 0xF4, 0xE8, 255);
    private static readonly UiColor CoordsColor = new(0xA9, 0xA9, 0xA9, 255);
    private static readonly float[] NotchX = [216f, 247f, 279f];

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        UiColor white = UiColor.White;

        // Filler between the groups, then the base plates.
        if (s.TryGet(UiSpriteNames.GapFill, out UiSprite gap))
        {
            if (l.LeftGap.W > 0f)
            {
                c.Tile(gap, l.LeftGap, white);
            }

            if (l.RightGap.W > 0f)
            {
                c.Tile(gap, l.RightGap, white);
            }
        }

        Sprite(c, s, UiSpriteNames.BaseLeft, l.LeftGroup, white);
        Sprite(c, s, UiSpriteNames.BaseCentre, l.CentreGroup, white);
        Sprite(c, s, UiSpriteNames.BaseRight, l.RightGroup, white);

        BuildLeft(c, v, l, s);
        BuildRight(c, l, s);
        BuildCentre(c, i, v, l, s, a);

        // Dragons draw above everything else in the bar.
        Sprite(c, s, UiSpriteNames.DragonLeft, l.FromUnits(l.LeftGroup, 0f, -46f, 107f, 46f), white);
        Sprite(c, s, UiSpriteNames.DragonRight, l.FromUnits(l.RightGroup, 533f - RightStart, -46f, 107f, 46f), white);
    }

    private static void BuildLeft(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        UiColor white = UiColor.White;
        UiRect g = l.LeftGroup;

        // Inert stone slab (no icon), the coordinates plate, an inert gauge with its caps.
        Sprite(c, s, "hud.slab.normal", l.FromUnits(g, 5f, 1f, 56f, 22f), white);
        UiRect coords = l.FromUnits(g, 5f, 24f, 56f, 22f);
        Sprite(c, s, UiSpriteNames.CoordsPlate, coords, white);
        Sprite(c, s, UiSpriteNames.GaugeEmpty, l.FromUnits(g, 73f, 5f, 15f, 35f), white);
        Sprite(c, s, UiSpriteNames.GaugeCapLeft, l.FromUnits(g, 64f, 1f, 33f, 46f), white);

        // HP gem: the empty stone, then the full stone clipped from the top down to the fill level.
        UiRect gem = l.FromUnits(g, 98f, -2f, 50f, 50f);
        HudStatusSnapshot st = v.Status;
        float hp = st.MaxHealth <= 0 ? 0f : Math.Clamp(st.CurrentHealth / (float)st.MaxHealth, 0f, 1f);
        DrawGem(c, s, UiSpriteNames.GemHpEmpty, UiSpriteNames.GemHpFull, gem, hp);
        Sprite(c, s, UiSpriteNames.TalonLeft, l.FromUnits(g, 140f, 1f, 59f, 46f), white);

        // Text after the art so the talons and gem never cover it.
        UiTextStyle big = l.Scale >= 3f ? UiTextStyle.Large : UiTextStyle.Body;
        Span<char> buf = stackalloc char[48];
        if (buf.TryWrite(CultureInfo.InvariantCulture, $"{st.CurrentHealth}/{st.MaxHealth}", out int n))
        {
            PanelText.HaloCentred(c, buf[..n], gem.X + gem.W * 0.5f, gem.Bottom - PanelText.Height(big) - 4f * l.Scale, big, NumberColor);
        }

        // Coordinates: map X and Z, truncated, in two groups.
        Vector3 p = v.PlayerPosition;
        if (float.IsFinite(p.X) && float.IsFinite(p.Z))
        {
            int px = (int)MathF.Floor(Math.Clamp(p.X, -1e9f, 1e9f));
            int pz = (int)MathF.Floor(Math.Clamp(p.Z, -1e9f, 1e9f));
            if (buf.TryWrite(CultureInfo.InvariantCulture, $"{px} {pz}", out n))
            {
                PanelText.Centred(c, buf[..n], coords.X + coords.W * 0.5f, coords.Y + (coords.H - PanelText.Height(big)) * 0.5f, big, CoordsColor);
            }
        }
    }

    private static void BuildRight(IUiCanvas c, in UiLayout l, UiSpriteTable s)
    {
        UiColor white = UiColor.White;
        UiRect g = l.RightGroup;

        // Two inert slabs, an inert gauge and caps, and the MP gem as an empty stone with no number.
        Sprite(c, s, "hud.slab.normal", l.FromUnits(g, 579f - RightStart, 1f, 56f, 22f), white);
        Sprite(c, s, "hud.slab.normal", l.FromUnits(g, 579f - RightStart, 24f, 56f, 22f), white);
        Sprite(c, s, UiSpriteNames.GaugeEmpty, l.FromUnits(g, 552f - RightStart, 5f, 15f, 35f), white);
        Sprite(c, s, UiSpriteNames.GaugeCapRight, l.FromUnits(g, 543f - RightStart, 1f, 33f, 46f), white);
        Sprite(c, s, UiSpriteNames.GemMpEmpty, l.FromUnits(g, 492f - RightStart, -2f, 50f, 50f), white);
        Sprite(c, s, UiSpriteNames.TalonRight, l.FromUnits(g, 441f - RightStart, 1f, 59f, 46f), white);
    }

    private static void BuildCentre(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        UiColor white = UiColor.White;
        UiRect g = l.CentreGroup;
        HudStatusSnapshot st = v.Status;

        // EXP: trough, fill clipped to the progress, section ticks, frame, cup with the level.
        UiRect trough = l.FromUnits(g, 221f - CentreStart, 5f, 199f, 6f);
        Sprite(c, s, UiSpriteNames.ExpTrough, trough, white);
        float exp = st.ExperienceToNextLevel <= 0 ? 0f : Math.Clamp(st.Experience / (float)st.ExperienceToNextLevel, 0f, 1f);
        if (exp > 0f && s.TryGet(UiSpriteNames.ExpFill, out UiSprite fill))
        {
            const float inset = 4f;
            UiRect span = l.FromUnits(g, 221f - CentreStart + inset, 5.5f, 199f - 2f * inset, 5f);
            c.Tile(fill, new UiRect(span.X, span.Y, MathF.Round(span.W * exp), span.H), white);
        }

        if (s.TryGet("exp.tick", out UiSprite tick))
        {
            for (int t = 1; t < 10; t++)
            {
                c.Sprite(tick, l.FromUnits(g, 221f - CentreStart + t * 19.9f, 5f, 1f, 6f), white);
            }
        }

        Sprite(c, s, UiSpriteNames.ExpFrame, l.FromUnits(g, 217f - CentreStart, 3f, 207f, 10f), white);
        UiRect cup = l.FromUnits(g, 420f - CentreStart, 3f, 12f, 12f);
        Sprite(c, s, UiSpriteNames.ExpCup, cup, white);

        // Q/W/E: empty sockets, no letters, no input.
        Sprite(c, s, "qwe.slot.q.normal", l.FromUnits(g, 199f - CentreStart, 18f, 33f, 28f), white);
        Sprite(c, s, "qwe.slot.w.normal", l.FromUnits(g, 235f - CentreStart, 18f, 29f, 28f), white);
        Sprite(c, s, "qwe.slot.e.normal", l.FromUnits(g, 266f - CentreStart, 18f, 28f, 28f), white);
        for (int k = 0; k < NotchX.Length; k++)
        {
            Sprite(c, s, "qwe.notch", l.FromUnits(g, NotchX[k] - CentreStart, 14f, 18f, 14f), white);
        }

        // Skill tile: the mode. Combat shows the default attack icon; Building shows the disabled tile.
        UiRect bracket = l.FromUnits(g, 304f - CentreStart, 11f, 32f, 36f);
        Sprite(c, s, "skill.bracket", bracket, white);
        bool combat = st.InteractionMode == PlayerInteractionMode.Combat;
        UiRect tile = l.FromUnits(g, 310.5f - CentreStart, 14.5f, 19f, 28f);
        Sprite(c, s, combat ? UiSpriteNames.SkillTile : "skill.tile.disabled", tile, white);
        if (combat)
        {
            Sprite(c, s, "skill.icon.default", l.FromUnits(g, 311.5f - CentreStart, 15.5f, 17f, 26f), white);
        }

        // Icon buttons: banner and lens are inert frames; the satchel closes an open overlay.
        Sprite(c, s, "iconbtn.plinth", l.FromUnits(g, 341f - CentreStart, 16.5f, 101f, 30f), white);
        UiRect banner = l.FromUnits(g, 346f - CentreStart, 18f, 28f, 28f);
        UiRect lens = l.FromUnits(g, 377f - CentreStart, 18f, 28f, 28f);
        UiRect satchel = l.FromUnits(g, 408f - CentreStart, 18f, 28f, 28f);
        Sprite(c, s, UiSpriteNames.IconButtonNormal, banner, white);
        Sprite(c, s, UiSpriteNames.IconButtonNormal, lens, white);
        string satchelFrame = UiSpriteNames.IconButtonNormal;
        if (v.InventoryOpen)
        {
            satchelFrame = i.Hovered(SatchelButtonId, satchel) ? UiSpriteNames.IconButtonHover : UiSpriteNames.IconButtonPressed;
            if (i.Button(SatchelButtonId, satchel))
            {
                a.CloseInventory();
            }
        }

        Sprite(c, s, satchelFrame, satchel, white);
        Sprite(c, s, UiSpriteNames.IconSatchel, l.FromUnits(g, 411f - CentreStart, 21f, 22f, 22f), white);

        // Text last: level in the cup, EXP percent when the trough is tall enough, mode above the tile.
        Span<char> buf = stackalloc char[32];
        float bodyH = PanelText.Height(UiTextStyle.Body);
        if (buf.TryWrite(CultureInfo.InvariantCulture, $"{st.Level}", out int n))
        {
            PanelText.HaloCentred(c, buf[..n], cup.X + cup.W * 0.5f, cup.Y + (cup.H - bodyH) * 0.5f, UiTextStyle.Body, NumberColor);
        }

        if (trough.H >= bodyH && buf.TryWrite(CultureInfo.InvariantCulture, $"{st.ExperiencePercent}%", out n))
        {
            PanelText.HaloCentred(c, buf[..n], trough.X + trough.W * 0.5f, trough.Y + (trough.H - bodyH) * 0.5f, UiTextStyle.Body, NumberColor);
        }

        PanelText.HaloCentred(c, st.InteractionMode.ToHudLabel(), bracket.X + bracket.W * 0.5f, bracket.Y - bodyH - 1f, UiTextStyle.Body, NumberColor);
    }

    private static void DrawGem(IUiCanvas c, UiSpriteTable s, string emptyName, string fullName, UiRect dst, float fill)
    {
        if (s.TryGet(emptyName, out UiSprite empty))
        {
            c.Sprite(empty, dst, UiColor.White);
        }

        if (fill > 0f && s.TryGet(fullName, out UiSprite full))
        {
            float h = dst.H * fill;
            c.SpriteClipped(full, new UiRect(dst.X, dst.Bottom - h, dst.W, h), new UiRect(0f, 1f - fill, 1f, fill), UiColor.White);
        }
    }

    private static void Sprite(IUiCanvas c, UiSpriteTable s, string name, UiRect dst, UiColor tint)
    {
        if (s.TryGet(name, out UiSprite sprite))
        {
            c.Sprite(sprite, dst, tint);
        }
    }
}

// Text drawing shared by the panels in this workstream.
internal static class PanelText
{
    private static readonly UiColor Halo = new(0x0A, 0x01, 0x01, 200);

    // Line height of the two baked sizes, in px.
    public static float Height(UiTextStyle style) => style == UiTextStyle.Large ? 26f : 13f;

    public static void Centred(IUiCanvas c, ReadOnlySpan<char> text, float centreX, float y, UiTextStyle style, UiColor color)
    {
        float w = c.MeasureText(text, style);
        c.Text(text, new Vector2(MathF.Round(centreX - w * 0.5f), MathF.Round(y)), style, color);
    }

    // Dark halo of four 1 px copies, then the text.
    public static void HaloCentred(IUiCanvas c, ReadOnlySpan<char> text, float centreX, float y, UiTextStyle style, UiColor color)
    {
        float w = c.MeasureText(text, style);
        float x = MathF.Round(centreX - w * 0.5f);
        y = MathF.Round(y);
        c.Text(text, new Vector2(x + 1f, y), style, Halo);
        c.Text(text, new Vector2(x - 1f, y), style, Halo);
        c.Text(text, new Vector2(x, y + 1f), style, Halo);
        c.Text(text, new Vector2(x, y - 1f), style, Halo);
        c.Text(text, new Vector2(x, y), style, color);
    }

    // Longest prefix of text that fits in maxWidth px.
    public static int FitLength(IUiCanvas c, ReadOnlySpan<char> text, UiTextStyle style, float maxWidth)
    {
        if (c.MeasureText(text, style) <= maxWidth)
        {
            return text.Length;
        }

        int lo = 0;
        int hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (c.MeasureText(text[..mid], style) <= maxWidth)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
