using Dream.Rendering.Hud;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The character window: the second stone window, left of the inventory (the art has no stat
// panel, so this is the combine window's frame with stat rows in its line boxes and no combine
// UI). Rows come from the view's StatRows: label on the left, "base +bonus = effective" on the
// right. Nothing here is interactive except the close button.
public static class CharacterWindow
{
    private const float RowX = 19f;
    private const float RowY = 50f;
    private const float RowPitch = 24f;
    private const float RowW = 152f;
    private const float RowH = 10f;
    private const int MaxRows = 6;

    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a)
    {
        if (!v.InventoryOpen)
        {
            return;
        }

        UiRect win = l.Character;
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);
        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), "Character", ItemTierStyle.Title);
        if (v.Inventory is Simulation.Inventory.PlayerInventorySnapshot inventory && inventory.PlayerName is { Length: > 0 } name)
        {
            // Server text: clipped to the strip, never a format string.
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), name.AsSpan(0, Math.Min(name.Length, 40)), ItemTierStyle.Body);
        }

        CharacterStatRow[] rows = v.StatRows ?? [];
        if (rows.Length == 0)
        {
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, RowY, RowW, RowH), "No data", ItemTierStyle.Dim);
        }

        int count = Math.Min(rows.Length, MaxRows);
        for (int n = 0; n < count; n++)
        {
            DrawRow(c, s, l, win, rows[n], RowY + (n * RowPitch));
        }

        bool live = !InventoryUiState.Current.ModalOpen;
        InventoryWindow.DrawCloseButton(c, i, l, s, a, win, InventoryWindow.IdCloseCharacter, live);
    }

    private static void DrawRow(IUiCanvas c, UiSpriteTable s, in UiLayout l, UiRect win, in CharacterStatRow row, float y)
    {
        UiRect box = l.FromUnits(win, RowX, y, RowW, RowH);
        if (s.TryGet(UiSpriteNames.WinLineBox, out UiSprite lineBox))
        {
            c.NineSlice(lineBox, box, UiColor.White);
        }

        float padX = 2f * l.Scale;
        float textY = box.Y + MathF.Floor((box.H - InventoryWindow.BodyTextPx) * 0.5f);
        c.PushClip(box);

        string label = row.Label ?? string.Empty;
        InventoryWindow.DrawText(c, label.AsSpan(0, Math.Min(label.Length, 24)), new Vector2(box.X + padX, textY), ItemTierStyle.Body);

        // "10 +2 = 12", or just "12" when there is no bonus. Built in a stack buffer.
        Span<char> mem = stackalloc char[64];
        UiTextBuilder b = new(mem);
        bool hasBonus = !string.IsNullOrEmpty(row.BonusText);
        if (hasBonus)
        {
            b.Append(row.BaseText ?? string.Empty);
            b.Append(" ");
            b.Append(row.BonusText);
            b.Append(" = ");
        }

        b.Append(row.EffectiveText ?? string.Empty);
        ReadOnlySpan<char> value = b.Text;
        float width = c.MeasureText(value, UiTextStyle.Body);
        InventoryWindow.DrawText(c, value, new Vector2(box.Right - padX - width, textY), hasBonus ? ItemTierStyle.Bonus : ItemTierStyle.Title);
        c.PopClip();

        if (!string.IsNullOrEmpty(row.Note))
        {
            string note = row.Note;
            InventoryWindow.DrawText(
                c,
                note.AsSpan(0, Math.Min(note.Length, 40)),
                new Vector2(box.X + padX, box.Bottom + l.Scale),
                ItemTierStyle.Dim);
        }
    }
}
