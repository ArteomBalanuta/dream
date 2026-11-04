using Dream.Rendering.Hud;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The always-on quest tracker (M6): a dark plate on the left edge with every active quest, its title and one
// line per objective ("Slay Wolf 1/2"). A quest whose objectives are all met says to return. Nothing is drawn
// without an active quest. Server text arrives cleaned and clamped by QuestHudState and is drawn as plain
// spans, cut to the plate.
public static class QuestLogPanel
{
    public const int MaxQuestsShown = 4;

    private static readonly UiColor Panel = new(0x0B, 0x07, 0x06, 170);
    private static readonly UiColor Rim = new(0x5A, 0x44, 0x2A, 220);
    private static readonly UiColor Shadow = new(0, 0, 0, 200);
    private static readonly UiColor Heading = new(0xD8, 0xB4, 0x5A, 255);
    private static readonly UiColor Title = new(0xF5, 0xDD, 0xA5, 255);
    private static readonly UiColor Body = new(0xD8, 0xD2, 0xC4, 255);
    private static readonly UiColor Met = new(0x5F, 0xD6, 0xA0, 255);

    public static void Build(IUiCanvas c, HudView v, in UiLayout l, QuestHudState? quests, PartyHudState? party = null)
    {
        if (quests is null || quests.Entries.Count == 0)
        {
            return;
        }

        UiTextStyle style = UiTextStyle.Body;
        float pad = MathF.Max(2f, MathF.Round(2.5f * l.Scale));
        // Leading, rim and shadow were a baked 1 to 2 px, so they stayed hairlines while the rows grew.
        float hair = UiText.ShadowPx(in l);
        // The objective lines are drawn by the dialogue window; it has to be given the same size the
        // rows were counted at, or the plate and its text disagree.
        UiSizedText t = UiText.For(in l, UiText.DefaultTextUnits);
        float rowH = PanelText.Height(c, style) + hair;
        float width = MathF.Max(MathF.Min(l.ViewportWidth * 0.3f, 150f * l.Scale), MathF.Min(l.ViewportWidth, 180f));

        int shown = 0;
        int rows = 1;
        foreach (QuestHudEntry entry in quests.Entries)
        {
            if (entry.Completed || shown >= MaxQuestsShown)
            {
                continue;
            }

            shown++;
            rows += 1 + entry.Objectives.Length + (entry.Ready ? 1 : 0);
        }

        if (shown == 0)
        {
            return;
        }

        float x = MathF.Round(6f * l.Scale);
        float height = (rows * rowH) + (2f * pad);
        float top = MathF.Round(l.ViewportHeight * 0.3f);
        top = MathF.Min(top, MathF.Max(0f, l.Bar.Y - height - hair));

        // The party plate shares this edge and can reach past 30 percent on a short viewport. It
        // measures its rows the same way this panel does, so the two plates never overlap.
        top = MathF.Max(top, PartyPanel.BottomOf(c, l, party) + hair);
        Frame(c, new UiRect(x, top, width, height), hair);

        float y = top + pad;
        DrawLine(c, "Quests", x + pad, y, width - (2f * pad), style, Heading, hair);
        y += rowH;
        int drawn = 0;
        foreach (QuestHudEntry entry in quests.Entries)
        {
            if (entry.Completed || drawn >= MaxQuestsShown)
            {
                continue;
            }

            drawn++;
            DrawLine(c, entry.Title, x + pad, y, width - (2f * pad), style, Title, hair);
            y += rowH;
            foreach (QuestHudObjective objective in entry.Objectives)
            {
                NpcDialogueWindow.DrawObjectiveLine(
                    c,
                    new UiRect(x + (2f * pad), y, width - (3f * pad), rowH),
                    objective,
                    objective.Have >= objective.Need ? Met : Body,
                    in t);
                y += rowH;
            }

            if (entry.Ready)
            {
                DrawLine(c, "Return to the quest giver", x + (2f * pad), y, width - (3f * pad), style, Met, hair);
                y += rowH;
            }
        }
    }

    private static void DrawLine(IUiCanvas c, ReadOnlySpan<char> text, float x, float y, float maxWidth, UiTextStyle style, UiColor color, float shadow)
    {
        text = text[..PanelText.FitLength(c, text, style, maxWidth)];
        float ty = MathF.Round(y);
        c.Text(text, new Vector2(x + shadow, ty + shadow), style, Shadow);
        c.Text(text, new Vector2(x, ty), style, color);
    }

    private static void Frame(IUiCanvas c, UiRect r, float t)
    {
        c.Solid(r, Panel);
        c.Solid(new UiRect(r.X, r.Y, r.W, t), Rim);
        c.Solid(new UiRect(r.X, r.Bottom - t, r.W, t), Rim);
        c.Solid(new UiRect(r.X, r.Y, t, r.H), Rim);
        c.Solid(new UiRect(r.Right - t, r.Y, t, r.H), Rim);
    }
}
