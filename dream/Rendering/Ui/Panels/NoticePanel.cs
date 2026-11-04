using Dream.Rendering.Hud;
using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// Centred notices on flat translucent bands (on the whole screen, below the tooltips), and the
// death overlay. Loot lines sit on a row lattice, newest on the entry row and older lines one row
// up each; the rejection and world-transfer lines take the rows below. Server text is hostile: it
// is passed as spans (a '%' or '{' stays literal) and clipped by width.
public static class NoticePanel
{
    public const float EntryRowUnits = 89f;
    public const float RowPitchUnits = 13f;
    public const float MaxWidthFraction = 0.8f;

    // Narrower than this and the strip beside the windows is not worth using.
    public const float MinFreeWidthPixels = 240f;

    private static readonly UiColor Gold = new(0xF5, 0xDD, 0xA5, 255);
    private static readonly UiColor Error = new(0xF4, 0x66, 0x50, 255);
    private static readonly UiColor Common = new(0xF9, 0xF8, 0xFA, 255);
    private static readonly UiColor Uncommon = new(0xF0, 0x8F, 0xB0, 255);
    private static readonly UiColor Rare = new(0x5F, 0xD6, 0xA0, 255);
    private static readonly UiColor Band = new(0, 0, 0, 128);
    private static readonly UiColor Dim = new(0, 0, 0, 110);

    public static UiColor RarityColor(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Common => Common,
        ItemRarity.Uncommon => Uncommon,
        ItemRarity.Rare => Rare,
        _ => Gold
    };

    public static void Build(IUiCanvas c, HudView v, in UiLayout l)
    {
        float pad = MathF.Max(1f, MathF.Round(1.8f * l.Scale));
        float pitch = MathF.Max(RowPitchUnits * l.Scale, PanelText.Height(c, UiTextStyle.Body) + 2f * pad);
        float centreX = l.ViewportWidth * 0.5f;
        float maxWidth = l.ViewportWidth * MaxWidthFraction;
        if (v.InventoryOpen && l.Character.X >= MinFreeWidthPixels)
        {
            // The windows stand right of the middle: the notices centre in the free strip to their
            // left instead of running over the stat rows.
            centreX = l.Character.X * 0.5f;
            maxWidth = l.Character.X * 0.95f;
        }

        float entryY = EntryRowUnits * l.Scale;

        LootFeedbackSystem? loot = v.Loot;
        int count = loot?.Count ?? 0;
        for (int k = 0; k < count; k++)
        {
            float y = entryY - (count - 1 - k) * pitch;
            Line(c, loot!.GetLine(k), centreX, y, pad, maxWidth, RarityColor(loot.GetRarity(k)));
        }

        int below = 1;
        if (!string.IsNullOrEmpty(v.RejectionMessage))
        {
            Line(c, v.RejectionMessage, centreX, entryY + below * pitch, pad, maxWidth, Error);
            below++;
        }

        WorldTransferHudState transfer = v.WorldTransfer;
        if (transfer.IsVisible)
        {
            Span<char> buf = stackalloc char[96];
            int kilobytes = (int)Math.Min(transfer.KilobytesReceived, int.MaxValue);
            int seconds = (int)Math.Min(transfer.WholeSecondsElapsed, int.MaxValue);
            if (TryWriteTransferLine(buf, kilobytes, seconds, out int n))
            {
                Line(c, buf[..n], centreX, entryY + below * pitch, pad, maxWidth, Gold);
                below++;
            }

            Line(c, WorldTransferHudState.DetailLine, centreX, entryY + below * pitch, pad, maxWidth, Gold);
        }

        if (v.Status.IsDead)
        {
            Death(c, v, l, centreX);
        }
    }

    // "Receiving world... 412 KB, 6 s" through int.TryFormat: the interpolated TryWrite boxes each
    // number while it runs in the PGO instrumented tier.
    private static bool TryWriteTransferLine(Span<char> buf, int kilobytes, int seconds, out int written)
    {
        written = 0;
        if (!TryAppend(buf, "Receiving world... ", ref written) ||
            !TryAppendInt(buf, kilobytes, ref written) ||
            !TryAppend(buf, " KB, ", ref written) ||
            !TryAppendInt(buf, seconds, ref written) ||
            !TryAppend(buf, " s", ref written))
        {
            written = 0;
            return false;
        }

        return true;
    }

    private static bool TryAppend(Span<char> buf, ReadOnlySpan<char> text, ref int written)
    {
        if (!text.TryCopyTo(buf[written..]))
        {
            return false;
        }

        written += text.Length;
        return true;
    }

    private static bool TryAppendInt(Span<char> buf, int value, ref int written)
    {
        if (!PanelText.TryWriteInt(buf[written..], value, out int n))
        {
            return false;
        }

        written += n;
        return true;
    }

    private static void Death(IUiCanvas c, HudView v, in UiLayout l, float centreX)
    {
        // The dim would sit over the inventory windows, which still work while dead (the server allows
        // inventory actions then), so it steps aside while they are open.
        if (!v.InventoryOpen)
        {
            c.Solid(new UiRect(0f, 0f, l.ViewportWidth, l.ViewportHeight), Dim);
        }

        float y = l.ViewportHeight * 0.38f;
        // The halo keeps up with the glyphs: a 1 px outline around a 76 px title is not an outline.
        float halo = UiText.ShadowPx(in l);
        PanelText.HaloCentred(c, HudStatusSnapshot.DeathTitle, centreX, y, UiTextStyle.Large, Error, halo);
        PanelText.HaloCentred(c, v.Status.RespawnLabel, centreX, y + PanelText.Height(c, UiTextStyle.Large) + 8f * l.Scale, UiTextStyle.Body, Gold, halo);
    }

    private static void Line(IUiCanvas c, ReadOnlySpan<char> text, float centreX, float y, float pad, float maxWidth, UiColor color)
    {
        if (text.IsEmpty)
        {
            return;
        }

        UiTextStyle style = UiTextStyle.Body;
        text = text[..PanelText.FitLength(c, text, style, maxWidth)];
        float w = c.MeasureText(text, style);
        float h = PanelText.Height(c, style);
        float x = MathF.Round(centreX - w * 0.5f);
        c.Solid(new UiRect(x - pad, y - pad, w + 2f * pad, h + 2f * pad), Band);
        c.Text(text, new Vector2(x, MathF.Round(y)), style, color);
    }
}
