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
        // Art first (frame, line boxes, close button), then every line of text, so the window is two
        // draw commands. The text is cut to fit instead of clipped, which would split the command.
        InventoryWindow.DrawFrame(c, s, l, win, inventoryKind: false);
        CharacterStatRow[] rows = v.StatRows ?? [];
        int count = Math.Min(rows.Length, MaxRows);
        for (int n = 0; n < count; n++)
        {
            if (s.TryGet(UiSpriteNames.WinLineBox, out UiSprite lineBox))
            {
                c.NineSlice(lineBox, l.FromUnits(win, RowX, RowY + (n * RowPitch), RowW, RowH), UiColor.White);
            }
        }

        bool live = !InventoryUiState.Current.ModalOpen;
        InventoryWindow.DrawCloseButton(c, i, l, s, a, win, InventoryWindow.IdCloseCharacter, live);

        InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 8f, 120f, 8f), "Character", ItemTierStyle.Title);
        if (v.Inventory is Simulation.Inventory.PlayerInventorySnapshot inventory && inventory.PlayerName is { Length: > 0 } name)
        {
            // Server text: cut to the strip, never a format string.
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, 35f, 20f, 119f, 8f), name.AsSpan(0, Math.Min(name.Length, 40)), ItemTierStyle.Body);
        }

        if (rows.Length == 0)
        {
            InventoryWindow.DrawTextCentered(c, l.FromUnits(win, RowX, RowY, RowW, RowH), "No data", ItemTierStyle.Dim);
        }

        for (int n = 0; n < count; n++)
        {
            DrawRow(c, l, win, rows[n], RowY + (n * RowPitch));
        }
    }

    private static void DrawRow(IUiCanvas c, in UiLayout l, UiRect win, in CharacterStatRow row, float y)
    {
        UiRect box = l.FromUnits(win, RowX, y, RowW, RowH);
        float padX = 2f * l.Scale;
        float textY = box.Y + MathF.Floor((box.H - InventoryWindow.BodyHeight(c)) * 0.5f);

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
        float inner = box.W - (2f * padX);
        value = value[..PanelText.FitLength(c, value, UiTextStyle.Body, inner)];
        float width = c.MeasureText(value, UiTextStyle.Body);

        // The label gets what the value leaves, with a gap of one space.
        string label = row.Label ?? string.Empty;
        ReadOnlySpan<char> labelText = label.AsSpan(0, Math.Min(label.Length, 24));
        labelText = labelText[..PanelText.FitLength(c, labelText, UiTextStyle.Body, MathF.Max(0f, inner - width - 2f))];
        InventoryWindow.DrawText(c, labelText, new Vector2(box.X + padX, textY), ItemTierStyle.Body);
        InventoryWindow.DrawText(c, value, new Vector2(box.Right - padX - width, textY), hasBonus ? ItemTierStyle.Bonus : ItemTierStyle.Title);

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
