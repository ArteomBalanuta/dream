using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Rendering.Ui.Panels;

// The six quickbar tiles (M4), drawn over the bar's own sockets: Q, W and E are slots 1 to 3, the skill
// tile is slot 4, the banner and lens frames are slots 5 and 6. A bound tile sits in a dark recess with
// a bronze rim; an empty one leaves the art untouched. Each tile can show, from the bottom up: the icon
// (the item's own, a skill's if the art has one, otherwise two letters on a dark plate), a cold tint when
// the entry cannot be used now, and a dark cooldown veil that drains from the top with the seconds left.
// The key digit stands in the top-left corner, the number of items in the bag in the bottom-right.
// With the inventory open a click on a tile only reports IHudActions.Quickbar; the scene sends the bind.
// Draws nothing without a quickbar state, and allocates nothing per frame.
public static class QuickbarPanel
{
    // Widget ids, clear of the satchel (0x5B01).
    internal const int IdTile = 0x5B10;

    private const float CentreStart = UiLayout.LeftGroupUnits;

    // Where each tile's icon sits, in bar units: its centre and edge. The first three are the Q, W and E
    // sockets, then the skill tile, the banner and the lens.
    private static readonly float[] CentreX = [215.5f, 249.5f, 280f, 320f, 360f, 391f];
    private static readonly float[] CentreY = [32f, 32f, 32f, 28.5f, 32f, 32f];
    private static readonly float[] Edge = [20f, 20f, 20f, 17f, 20f, 20f];

    private static readonly UiColor Recess = new(0x0C, 0x08, 0x07, 235);
    private static readonly UiColor GlyphPlate = new(0x22, 0x16, 0x12, 255);
    private static readonly UiColor Rim = new(0x7A, 0x5C, 0x32, 235);
    private static readonly UiColor RimHover = new(0xD8, 0xB4, 0x5A, 255);
    private static readonly UiColor ColdTint = new(0x05, 0x08, 0x18, 150);
    private static readonly UiColor CooldownVeil = new(0x04, 0x02, 0x02, 190);
    private static readonly UiColor VeilEdge = new(0xA8, 0x44, 0x22, 210);
    private static readonly UiColor DigitColor = new(0xC8, 0xA8, 0x60, 255);
    private static readonly UiColor GlyphColor = new(0xD8, 0xC9, 0xA0, 255);
    private static readonly UiColor CountColor = new(0xF8, 0xF4, 0xE8, 255);
    private static readonly UiColor EmptyCountColor = new(0xA0, 0x40, 0x38, 255);
    private static readonly UiColor SecondsColor = new(0xF8, 0xF4, 0xE8, 255);

    // The tile's icon rect in pixels, whole pixels so the rim and the veil line up.
    internal static UiRect TileRect(in UiLayout l, int slot)
    {
        float edge = Edge[slot];
        UiRect r = l.FromUnits(l.CentreGroup, CentreX[slot] - edge * 0.5f - CentreStart, CentreY[slot] - edge * 0.5f, edge, edge);
        return new UiRect(MathF.Round(r.X), MathF.Round(r.Y), MathF.Max(1f, MathF.Round(r.W)), MathF.Max(1f, MathF.Round(r.H)));
    }

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        if (v.Quickbar is not QuickbarHudState bar)
        {
            return;
        }

        bool modeBlocks = v.Status.InteractionMode != PlayerInteractionMode.Combat;
        for (int slot = 0; slot < QuickbarHudState.SlotCount; slot++)
        {
            UiRect r = TileRect(in l, slot);
            bool empty = bar.IsEmpty(slot);
            bool hovered = v.InventoryOpen && i.Hovered(IdTile + slot, r);
            if (v.InventoryOpen && i.Button(IdTile + slot, r))
            {
                a.Quickbar(slot);
            }

            if (empty)
            {
                // The socket's own art stays; with the inventory open the pointer still finds it.
                if (hovered)
                {
                    DrawRim(c, r, l.Scale, RimHover);
                }

                continue;
            }

            c.Solid(r, Recess);
            string iconName = bar.IconNameOf(slot);
            if (iconName.Length > 0 && s.TryGet(iconName, out UiSprite icon))
            {
                DrawFitted(c, icon, r);
            }
            else
            {
                float inset = MathF.Max(1f, MathF.Round(l.Scale));
                c.Solid(new UiRect(r.X + inset, r.Y + inset, r.W - 2f * inset, r.H - 2f * inset), GlyphPlate);
            }

            bool coldNow = bar.IsUnavailable(slot) || (modeBlocks && !bar.IsHeal(slot) && bar.Slot(slot).Kind == QuickbarEntryKind.Skill);
            if (coldNow)
            {
                c.Solid(r, ColdTint);
            }

            ulong remaining = bar.RemainingTicks(slot, bar.NowTick, out int total);
            if (remaining > 0 && total > 0)
            {
                float fraction = Math.Clamp(remaining / (float)total, 0f, 1f);
                float h = MathF.Max(1f, MathF.Round(r.H * fraction));
                c.Solid(new UiRect(r.X, r.Y, r.W, h), CooldownVeil);
                c.Solid(new UiRect(r.X, r.Y + h - 1f, r.W, 1f), VeilEdge);
            }

            DrawRim(c, r, l.Scale, hovered ? RimHover : Rim);
        }
    }

    // Digits, letters, counts and seconds, in the bar's text pass after the art.
    public static void BuildText(IUiCanvas c, HudView v, in UiLayout l, UiSpriteTable s)
    {
        if (v.Quickbar is not QuickbarHudState bar)
        {
            return;
        }

        float bodyH = PanelText.Height(c, UiTextStyle.Body);
        Span<char> buf = stackalloc char[16];
        for (int slot = 0; slot < QuickbarHudState.SlotCount; slot++)
        {
            UiRect r = TileRect(in l, slot);
            bool empty = bar.IsEmpty(slot);

            buf[0] = (char)('1' + slot);
            PanelText.HaloCentred(c, buf[..1], r.X + 4f * l.Scale, r.Y + 1f, UiTextStyle.Body, DigitColor);
            if (empty)
            {
                continue;
            }

            ulong remaining = bar.RemainingTicks(slot, bar.NowTick, out int total);
            bool cooling = remaining > 0 && total > 0;
            int n;
            if (cooling)
            {
                ulong seconds = (remaining + SimulationClock.AuthoritativeTicksPerSecond - 1) / SimulationClock.AuthoritativeTicksPerSecond;
                if (PanelText.TryWriteInt(buf, (int)Math.Min(seconds, 9999UL), out n))
                {
                    PanelText.HaloCentred(c, buf[..n], r.X + r.W * 0.5f, r.Y + (r.H - bodyH) * 0.5f, UiTextStyle.Body, SecondsColor);
                }
            }
            else if (!HasIcon(bar, s, slot) && (n = WriteInitials(buf, bar.LabelOf(slot))) > 0)
            {
                PanelText.HaloCentred(c, buf[..n], r.X + r.W * 0.5f, r.Y + (r.H - bodyH) * 0.5f, UiTextStyle.Body, GlyphColor);
            }

            if (bar.Slot(slot).Kind == QuickbarEntryKind.Item)
            {
                int count = bar.ItemCount(slot);
                if (PanelText.TryWriteInt(buf, Math.Min(count, 999), out n))
                {
                    float w = c.MeasureText(buf[..n], UiTextStyle.Body);
                    PanelText.HaloCentred(c, buf[..n], r.Right - w * 0.5f - 1f, r.Bottom - bodyH, UiTextStyle.Body, count > 0 ? CountColor : EmptyCountColor);
                }
            }
        }
    }

    private static bool HasIcon(QuickbarHudState bar, UiSpriteTable s, int slot)
    {
        string name = bar.IconNameOf(slot);
        return name.Length > 0 && s.TryGet(name, out _);
    }

    // Two letters from a display name: the first letters of its first two words, or its first two
    // letters for one word ("Cleave" is "Cl", "Health Potion" is "HP"). 0 when the name has no letters.
    internal static int WriteInitials(Span<char> buf, string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || buf.Length < 2)
        {
            return 0;
        }

        ReadOnlySpan<char> text = label.AsSpan().Trim();
        buf[0] = char.ToUpperInvariant(text[0]);
        int space = text.IndexOf(' ');
        if (space >= 0)
        {
            ReadOnlySpan<char> rest = text[(space + 1)..].TrimStart();
            if (!rest.IsEmpty)
            {
                buf[1] = char.ToUpperInvariant(rest[0]);
                return 2;
            }
        }

        if (text.Length > 1)
        {
            buf[1] = char.ToLowerInvariant(text[1]);
            return 2;
        }

        return 1;
    }

    // The sprite as large as the square allows, centred, aspect kept.
    private static void DrawFitted(IUiCanvas c, in UiSprite sprite, UiRect box)
    {
        if (sprite.W <= 0 || sprite.H <= 0)
        {
            return;
        }

        float scale = MathF.Min(box.W / sprite.W, box.H / sprite.H);
        float w = MathF.Round(sprite.W * scale);
        float h = MathF.Round(sprite.H * scale);
        c.Sprite(sprite, new UiRect(MathF.Round(box.X + (box.W - w) * 0.5f), MathF.Round(box.Y + (box.H - h) * 0.5f), w, h), UiColor.White);
    }

    private static void DrawRim(IUiCanvas c, UiRect r, float scale, UiColor color)
    {
        float t = MathF.Max(1f, MathF.Round(scale * 0.5f));
        c.Solid(new UiRect(r.X, r.Y, r.W, t), color);
        c.Solid(new UiRect(r.X, r.Bottom - t, r.W, t), color);
        c.Solid(new UiRect(r.X, r.Y + t, t, r.H - 2f * t), color);
        c.Solid(new UiRect(r.Right - t, r.Y + t, t, r.H - 2f * t), color);
    }
}
