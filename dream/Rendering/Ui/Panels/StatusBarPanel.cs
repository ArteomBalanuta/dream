using System.Globalization;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The bottom bar: left, centre and right groups on the three base sprites, the tiled filler between
// them, then the dragons last. Positions are the art's own layout in units (u), read from the
// generator: a rect given in bar units x is group-relative x minus the group's start (0, 199, 441).
// Real data only: HP gem fill, EXP trough and level, mode, coordinates, satchel. The six sockets of the
// centre group (Q, W, E, the skill tile, banner and lens) are the quickbar's tiles: QuickbarPanel draws
// a bound entry over each and reports a click while the inventory is open. An empty slot, and a bar that
// was never given a state, leave the socket as art. What the game has no system for (the two gauges, the
// stone slabs) is drawn as an empty art socket with no number, no key letter and no input. The MP gem
// follows the server's mana once it has sent any, and ManaGemFill until then.
//
// The gems are liquid: the panel hands the canvas the eased level the surface computed (HudView
// HealthLevel and ManaLevel) and the canvas draws the stone in its Gem mode, one quad on the Hud page.
// Draw order inside a group: the bezel under the gem, the gem, the talons (they bite over the stone), then
// the gem's additive light on the iron and the talons. Numerals are sprites on the same page, drawn after
// the dragons, so the whole bar's art stays in one draw command and the text follows in another.
public static class StatusBarPanel
{
    public const int SatchelButtonId = 0x5B01;

    // How full the MP gem is drawn while no mana is known (HudStatusSnapshot.MaxMana is 0: the menus, and
    // the first moments of a session before the server's first vitals). A visual choice, not data. The
    // stone is drawn full and lit so it does not read as a dead black socket next to the HP gem. 0 draws
    // it empty again.
    public const float ManaGemFill = 1f;

    private const float CentreStart = UiLayout.LeftGroupUnits;
    private const float RightStart = UiLayout.RightGroupStartUnits;

    // Art layout in bar units (left side; the right side mirrors across the 640 u bar). The gem is
    // 50 x 50 at (98, -2); the bezel and the spill are centred on it.
    private const float GemX = 98f;
    private const float GemY = -2f;
    private const float GemSize = 50f;
    private const float SettingW = 62f;
    private const float SettingH = 56f;
    private const float SpillW = 128f;
    private const float SpillH = 64f;
    private const float TalonX = 128f;
    private const float TalonY = -3f;
    private const float TalonW = 71f;
    private const float TalonH = 52f;
    private const float DragonY = -52f;
    private const float DragonW = 118f;
    private const float DragonH = 58f;

    // The two gems' glint sweeps run half a period apart.
    private const float ManaSweepOffsetSeconds = GemAnimation.SweepPeriodSeconds * 0.5f;

    // Bar numerals ("hudnum.*"): the gap between two glyphs in units, and the smallest shrink that
    // still reads (below it the text fallback is used).
    private const float NumeralTracking = 0.5f;
    private const float MinNumeralScale = 0.5f;

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
        BuildRight(c, v, l, s);
        BuildCentre(c, i, v, l, s, a);

        // Dragons draw above everything else in the bar's art. Their bottom edge is 6 u over the plate.
        Sprite(c, s, UiSpriteNames.DragonLeft, l.FromUnits(l.LeftGroup, 0f, DragonY, DragonW, DragonH), white);
        Sprite(c, s, UiSpriteNames.DragonRight, l.FromUnits(l.RightGroup, UiLayout.BarWidthUnits - DragonW - RightStart, DragonY, DragonW, DragonH), white);

        // The bar's numerals are Hud-page sprites, so they join the art's draw command; the talons,
        // gem and dragons are under them. A number the art has no glyph for falls back to text.
        bool hpNumerals = TryDrawHealthNumerals(c, v, l, s);
        bool levelNumerals = TryDrawLevelNumerals(c, v, l, s);
        bool manaNumerals = TryDrawManaNumerals(c, v, l, s);

        // All the bar's text last, in one draw command: no text overlaps the dragons, and the talons
        // and gem must never cover it.
        BuildLeftText(c, v, l, hpNumerals);
        BuildCentreText(c, v, l, s, levelNumerals);
        BuildRightText(c, v, l, manaNumerals);
    }

    private static void BuildLeft(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        UiColor white = UiColor.White;
        UiRect g = l.LeftGroup;

        // Inert stone slab (no icon), the coordinates plate, an inert gauge with its caps.
        Sprite(c, s, UiSpriteNames.SlabNormal, l.FromUnits(g, 5f, 1f, 56f, 22f), white);
        UiRect coords = l.FromUnits(g, 5f, 24f, 56f, 22f);
        Sprite(c, s, UiSpriteNames.CoordsPlate, coords, white);
        Sprite(c, s, UiSpriteNames.GaugeEmpty, l.FromUnits(g, 73f, 5f, 15f, 35f), white);
        Sprite(c, s, UiSpriteNames.GaugeCapLeft, l.FromUnits(g, 64f, 1f, 33f, 46f), white);

        // HP gem: the bezel under it, the liquid stone at the eased level, then the talons over its edge
        // and the gem's light on the iron and the talons.
        UiRect gem = l.FromUnits(g, GemX, GemY, GemSize, GemSize);
        Sprite(c, s, UiSpriteNames.GemSettingLeft, CentredOnGem(l, g, GemX, SettingW, SettingH), white);
        DrawGem(c, s, UiGemKind.Health, gem, v.HealthLevel, v.GemPulse, v.AnimationSeconds, 0f);
        Sprite(c, s, UiSpriteNames.TalonLeft, l.FromUnits(g, TalonX, TalonY, TalonW, TalonH), white);
        DrawSpill(c, s, UiSpriteNames.GemHpSpill, CentredOnGem(l, g, GemX, SpillW, SpillH), v.HealthLevel, v.GemPulse);
    }

    private static void BuildLeftText(IUiCanvas c, HudView v, in UiLayout l, bool hpNumeralsDrawn)
    {
        UiRect g = l.LeftGroup;
        UiRect coords = l.FromUnits(g, 5f, 24f, 56f, 22f);
        UiRect gem = l.FromUnits(g, GemX, GemY, GemSize, GemSize);
        HudStatusSnapshot st = v.Status;
        UiTextStyle big = l.Scale >= 3f ? UiTextStyle.Large : UiTextStyle.Body;
        Span<char> buf = stackalloc char[48];
        int n;
        if (!hpNumeralsDrawn && PanelText.TryWritePair(buf, st.CurrentHealth, '/', st.MaxHealth, out n))
        {
            PanelText.HaloCentred(c, buf[..n], gem.X + gem.W * 0.5f, gem.Bottom - PanelText.Height(c, big) - 4f * l.Scale, big, NumberColor);
        }

        // Coordinates: map X and Z, truncated, in two groups.
        Vector3 p = v.PlayerPosition;
        if (float.IsFinite(p.X) && float.IsFinite(p.Z))
        {
            int px = (int)MathF.Floor(Math.Clamp(p.X, -1e9f, 1e9f));
            int pz = (int)MathF.Floor(Math.Clamp(p.Z, -1e9f, 1e9f));
            if (PanelText.TryWritePair(buf, px, ' ', pz, out n))
            {
                PanelText.Centred(c, buf[..n], coords.X + coords.W * 0.5f, coords.Y + (coords.H - PanelText.Height(c, big)) * 0.5f, big, CoordsColor);
            }
        }
    }

    private static void BuildRight(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        UiColor white = UiColor.White;
        UiRect g = l.RightGroup;

        // Two inert slabs, an inert gauge and caps. The MP gem is drawn at ManaGemFill, lit, with no
        // number: the right side mirrors the left across the bar.
        Sprite(c, s, UiSpriteNames.SlabNormal, l.FromUnits(g, 579f - RightStart, 1f, 56f, 22f), white);
        Sprite(c, s, UiSpriteNames.SlabNormal, l.FromUnits(g, 579f - RightStart, 24f, 56f, 22f), white);
        Sprite(c, s, UiSpriteNames.GaugeEmpty, l.FromUnits(g, 552f - RightStart, 5f, 15f, 35f), white);
        Sprite(c, s, UiSpriteNames.GaugeCapRight, l.FromUnits(g, 543f - RightStart, 1f, 33f, 46f), white);
        float gemX = UiLayout.BarWidthUnits - GemX - GemSize - RightStart;
        UiRect gem = l.FromUnits(g, gemX, GemY, GemSize, GemSize);
        Sprite(c, s, UiSpriteNames.GemSettingRight, CentredOnGem(l, g, gemX, SettingW, SettingH), white);
        DrawGem(c, s, UiGemKind.Mana, gem, v.ManaLevel, 0f, v.AnimationSeconds, ManaSweepOffsetSeconds);
        Sprite(c, s, UiSpriteNames.TalonRight, l.FromUnits(g, UiLayout.BarWidthUnits - TalonX - TalonW - RightStart, TalonY, TalonW, TalonH), white);
        DrawSpill(c, s, UiSpriteNames.GemMpSpill, CentredOnGem(l, g, gemX, SpillW, SpillH), v.ManaLevel, 0f);
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

        if (s.TryGet(UiSpriteNames.ExpTick, out UiSprite tick))
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
        Sprite(c, s, UiSpriteNames.QweSlotQ, l.FromUnits(g, 199f - CentreStart, 18f, 33f, 28f), white);
        Sprite(c, s, UiSpriteNames.QweSlotW, l.FromUnits(g, 235f - CentreStart, 18f, 29f, 28f), white);
        Sprite(c, s, UiSpriteNames.QweSlotE, l.FromUnits(g, 266f - CentreStart, 18f, 28f, 28f), white);
        for (int k = 0; k < NotchX.Length; k++)
        {
            Sprite(c, s, UiSpriteNames.QweNotch, l.FromUnits(g, NotchX[k] - CentreStart, 14f, 18f, 14f), white);
        }

        // Skill tile: the mode. Combat shows the default attack icon; Building shows the disabled tile.
        UiRect bracket = l.FromUnits(g, 304f - CentreStart, 11f, 32f, 36f);
        Sprite(c, s, UiSpriteNames.SkillBracket, bracket, white);
        bool combat = st.InteractionMode == PlayerInteractionMode.Combat;
        UiRect tile = l.FromUnits(g, 310.5f - CentreStart, 14.5f, 19f, 28f);
        Sprite(c, s, combat ? UiSpriteNames.SkillTile : UiSpriteNames.SkillTileDisabled, tile, white);
        if (combat)
        {
            Sprite(c, s, UiSpriteNames.SkillIconDefault, l.FromUnits(g, 311.5f - CentreStart, 15.5f, 17f, 26f), white);
        }

        // Icon buttons: banner and lens are inert frames; the satchel closes an open overlay.
        Sprite(c, s, UiSpriteNames.IconButtonPlinth, l.FromUnits(g, 341f - CentreStart, 16.5f, 101f, 30f), white);
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

        // The quickbar's tiles over the six sockets above.
        QuickbarPanel.Build(c, i, v, l, s, a);
    }

    // Level in the cup, EXP percent when the trough is tall enough, and the mode label.
    private static void BuildCentreText(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s, bool levelNumeralsDrawn)
    {
        UiRect g = l.CentreGroup;
        HudStatusSnapshot st = v.Status;
        UiRect trough = l.FromUnits(g, 221f - CentreStart, 5f, 199f, 6f);
        UiRect cup = l.FromUnits(g, 420f - CentreStart, 3f, 12f, 12f);
        UiRect bracket = l.FromUnits(g, 304f - CentreStart, 11f, 32f, 36f);
        Span<char> buf = stackalloc char[32];
        float bodyH = PanelText.Height(c, UiTextStyle.Body);
        int n;
        if (!levelNumeralsDrawn && PanelText.TryWriteInt(buf, st.Level, out n))
        {
            PanelText.HaloCentred(c, buf[..n], cup.X + cup.W * 0.5f, cup.Y + (cup.H - bodyH) * 0.5f, UiTextStyle.Body, NumberColor);
        }

        if (trough.H >= bodyH && PanelText.TryWritePercent(buf, st.ExperiencePercent, out n))
        {
            PanelText.HaloCentred(c, buf[..n], trough.X + trough.W * 0.5f, trough.Y + (trough.H - bodyH) * 0.5f, UiTextStyle.Body, NumberColor);
        }

        // The mode label stands on top of the bar, centred over the skill tile: the EXP trough runs
        // across the space just above the tile, and the label used to sit on it.
        PanelText.HaloCentred(c, st.InteractionMode.ToHudLabel(), bracket.X + bracket.W * 0.5f, ModeLabelY(c, g, l), UiTextStyle.Body, NumberColor);

        QuickbarPanel.BuildText(c, v, l, s);
    }

    // Mana as text on the MP gem when the bar art has no numerals; nothing until the server has sent any.
    private static void BuildRightText(IUiCanvas c, HudView v, in UiLayout l, bool manaNumeralsDrawn)
    {
        HudStatusSnapshot st = v.Status;
        if (manaNumeralsDrawn || st.MaxMana <= 0)
        {
            return;
        }

        UiRect gem = ManaGemRect(l);
        UiTextStyle big = l.Scale >= 3f ? UiTextStyle.Large : UiTextStyle.Body;
        Span<char> buf = stackalloc char[48];
        if (PanelText.TryWritePair(buf, st.CurrentMana, '/', st.MaxMana, out int n))
        {
            PanelText.HaloCentred(c, buf[..n], gem.X + gem.W * 0.5f, gem.Bottom - PanelText.Height(c, big) - 4f * l.Scale, big, NumberColor);
        }
    }

    private static UiRect ManaGemRect(in UiLayout l) =>
        l.FromUnits(l.RightGroup, UiLayout.BarWidthUnits - GemX - GemSize - RightStart, GemY, GemSize, GemSize);

    // Top of the mode label: its bottom edge two scaled units above the bar group.
    internal static float ModeLabelY(IUiCanvas c, UiRect centreGroup, in UiLayout l) =>
        centreGroup.Y - PanelText.Height(c, UiTextStyle.Body) - (2f * l.Scale);

    // A rect w x h u centred on the gem whose left edge is gemX (group-relative u).
    private static UiRect CentredOnGem(in UiLayout l, UiRect group, float gemX, float w, float h) =>
        l.FromUnits(group, gemX + GemSize * 0.5f - w * 0.5f, GemY + GemSize * 0.5f - h * 0.5f, w, h);

    // One gem: a single liquid quad when the art has the gem's empty glass, level map and glints, so the
    // canvas does the fill, meniscus, glint sweep and pulse in its Gem mode. Art without them (and any
    // table that lacks the family) keeps the plain look: the empty stone, then the full stone clipped
    // from the top down to the level.
    private static void DrawGem(IUiCanvas c, UiSpriteTable s, UiGemKind kind, UiRect dst, float level, float pulse, float seconds, float sweepOffsetSeconds)
    {
        level = Saturate(level);
        if (UiBatch.TryGetGemFamily(s, kind, out UiSprite full, out _, out _, out _))
        {
            c.Gem(full, dst, kind, level, GemAnimation.Phase(seconds), Saturate(pulse), GemAnimation.Sweep(seconds, sweepOffsetSeconds), UiColor.White);
            return;
        }

        string emptyName = kind == UiGemKind.Health ? UiSpriteNames.GemHpEmpty : UiSpriteNames.GemMpEmpty;
        string fullName = kind == UiGemKind.Health ? UiSpriteNames.GemHpFull : UiSpriteNames.GemMpFull;
        if (s.TryGet(emptyName, out UiSprite empty))
        {
            c.Sprite(empty, dst, UiColor.White);
        }

        if (level > 0f && s.TryGet(fullName, out UiSprite stone))
        {
            float h = dst.H * level;
            c.SpriteClipped(stone, new UiRect(dst.X, dst.Bottom - h, dst.W, h), new UiRect(0f, 1f - level, 1f, level), UiColor.White);
        }
    }

    // The gem's light on the iron and the talons: the spill sprite added over the bar, its alpha scaling
    // with the level (and a little with the pulse). Nothing when the art has no spill or the gem is empty.
    private static void DrawSpill(IUiCanvas c, UiSpriteTable s, string name, UiRect dst, float level, float pulse)
    {
        float alpha = MathF.Min(Saturate(level) * (1f + 0.25f * Saturate(pulse)), 1f);
        if (alpha * 255f < 0.5f || !s.TryGet(name, out UiSprite spill))
        {
            return;
        }

        c.Additive(spill, dst, new UiColor(255, 255, 255, (byte)MathF.Round(alpha * 255f)));
    }

    // 0..1, NaN as 0.
    private static float Saturate(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 1f);

    // Health as bar numerals ("73/120") centred above the gem's bottom edge. False when the art has no
    // numerals (or no glyph for a character, such as a minus sign): the text path draws it instead.
    private static bool TryDrawHealthNumerals(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        Span<char> buf = stackalloc char[48];
        if (!PanelText.TryWritePair(buf, v.Status.CurrentHealth, '/', v.Status.MaxHealth, out int n))
        {
            return false;
        }

        UiRect gem = l.FromUnits(l.LeftGroup, GemX, GemY, GemSize, GemSize);
        return TryDrawNumerals(c, s, l, buf[..n], gem.X + gem.W * 0.5f, gem.Bottom - 4f * l.Scale, false, gem.W, 14f * l.Scale);
    }

    // Mana as bar numerals on the MP gem, as the health ones sit on the HP gem. Nothing is drawn, and false
    // is returned, until the server has sent a maximum.
    private static bool TryDrawManaNumerals(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        if (v.Status.MaxMana <= 0)
        {
            return false;
        }

        Span<char> buf = stackalloc char[48];
        if (!PanelText.TryWritePair(buf, v.Status.CurrentMana, '/', v.Status.MaxMana, out int n))
        {
            return false;
        }

        UiRect gem = ManaGemRect(in l);
        return TryDrawNumerals(c, s, l, buf[..n], gem.X + gem.W * 0.5f, gem.Bottom - 4f * l.Scale, false, gem.W, 14f * l.Scale);
    }

    // The level as bar numerals, shrunk to sit inside the EXP cup.
    private static bool TryDrawLevelNumerals(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        Span<char> buf = stackalloc char[32];
        if (!PanelText.TryWriteInt(buf, v.Status.Level, out int n))
        {
            return false;
        }

        UiRect cup = l.FromUnits(l.CentreGroup, 420f - CentreStart, 3f, 12f, 12f);
        return TryDrawNumerals(c, s, l, buf[..n], cup.X + cup.W * 0.5f, cup.Y + cup.H * 0.5f, true, cup.W * 0.85f, cup.H * 0.75f);
    }

    // Draws text made of "hudnum" glyph sprites (digits and a slash) at the art's own size, shrunk to fit
    // maxW x maxH px, and centred on centreX. anchorY is the bottom edge, or the centre when
    // anchorIsCentre. Nothing is drawn, and false is returned, when a character has no glyph or the
    // numbers would shrink below MinNumeralScale.
    private static bool TryDrawNumerals(IUiCanvas c, UiSpriteTable s, in UiLayout l, ReadOnlySpan<char> text, float centreX, float anchorY, bool anchorIsCentre, float maxW, float maxH)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        float texel = l.PixelsPerTexel;
        float tracking = NumeralTracking * l.Scale;
        float width = tracking * (text.Length - 1);
        float height = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            if (!TryNumeral(s, text[i], out UiSprite glyph))
            {
                return false;
            }

            width += glyph.W * texel;
            height = MathF.Max(height, glyph.H * texel);
        }

        if (width <= 0f || height <= 0f)
        {
            return false;
        }

        float scale = MathF.Min(1f, MathF.Min(maxW / width, maxH / height));
        if (scale < MinNumeralScale)
        {
            return false;
        }

        float bottom = MathF.Round(anchorIsCentre ? anchorY + height * scale * 0.5f : anchorY);
        float x = centreX - width * scale * 0.5f;
        for (int i = 0; i < text.Length; i++)
        {
            TryNumeral(s, text[i], out UiSprite glyph);
            float x0 = MathF.Round(x);
            float x1 = MathF.Round(x + glyph.W * texel * scale);
            float y0 = MathF.Round(bottom - glyph.H * texel * scale);
            c.Sprite(glyph, new UiRect(x0, y0, x1 - x0, bottom - y0), UiColor.White);
            x += (glyph.W * texel + tracking) * scale;
        }

        return true;
    }

    private static bool TryNumeral(UiSpriteTable s, char ch, out UiSprite glyph)
    {
        string? name = ch == '/' ? UiSpriteNames.HudNumSlash : ch is >= '0' and <= '9' ? UiSpriteNames.HudNum(ch - '0') : null;
        glyph = default;
        return name is not null && s.TryGet(name, out glyph);
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

    // Numbers go through int.TryFormat, not an interpolated TryWrite: the handler's generic
    // AppendFormatted<int> boxes its value while it runs in the PGO instrumented tier, which is
    // 24 B per number on every frame until tier 1 lands.
    public static bool TryWriteInt(Span<char> buf, int value, out int written) =>
        value.TryFormat(buf, out written, default, CultureInfo.InvariantCulture);

    public static bool TryWritePair(Span<char> buf, int first, char separator, int second, out int written)
    {
        written = 0;
        if (!TryWriteInt(buf, first, out int a) || a >= buf.Length)
        {
            return false;
        }

        buf[a] = separator;
        if (!TryWriteInt(buf[(a + 1)..], second, out int b))
        {
            return false;
        }

        written = a + 1 + b;
        return true;
    }

    public static bool TryWritePercent(Span<char> buf, int value, out int written)
    {
        if (!TryWriteInt(buf, value, out written) || written >= buf.Length)
        {
            written = 0;
            return false;
        }

        buf[written++] = '%';
        return true;
    }

    // Line height of the two baked sizes, in px, times the canvas's text scale (doubled glyphs at 4K).
    public static float Height(IUiCanvas c, UiTextStyle style) => (style == UiTextStyle.Large ? 26f : 13f) * c.TextScale;

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
