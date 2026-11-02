using Dream.Rendering.Hud;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// The party members with their health (M5 workstream A), down the left edge above the chat: a dark plate
// with a bronze rim, one row per member with the name (the leader's in gold), the level and a blood-red
// health bar in a blackened trough. A pending invite shows as one line asking for /accept or /decline.
// Draws nothing without members or an invite. The names were cleaned by the scene and are cut to the plate;
// they are drawn as plain spans, never format strings. The panel has no input of its own (the answer to an
// invite is a chat command), so intents is not used.
public static class PartyPanel
{
    private static readonly UiColor Plate = new(0x0B, 0x07, 0x06, 190);
    private static readonly UiColor Rim = new(0x5A, 0x44, 0x2A, 230);
    private static readonly UiColor RimInner = new(0x1C, 0x13, 0x0C, 230);
    private static readonly UiColor Trough = new(0x12, 0x08, 0x07, 255);
    private static readonly UiColor Blood = new(0x8A, 0x1E, 0x1E, 255);
    private static readonly UiColor BloodLow = new(0xB8, 0x2A, 0x20, 255);
    private static readonly UiColor Dead = new(0x3A, 0x30, 0x2C, 255);
    private static readonly UiColor LeaderName = new(0xD8, 0xB4, 0x5A, 255);
    private static readonly UiColor MemberName = new(0xD8, 0xD2, 0xC4, 255);
    private static readonly UiColor Dim = new(0xA6, 0x9C, 0x8A, 255);
    private static readonly UiColor Shadow = new(0, 0, 0, 200);

    public static void Build(IUiCanvas c, HudView v, in UiLayout l, PartyHudState? party, HudIntentQueue? intents)
    {
        _ = v;
        _ = intents;
        if (party is null || party.IsEmpty)
        {
            return;
        }

        UiTextStyle style = UiTextStyle.Body;
        float pad = MathF.Max(2f, MathF.Round(2.5f * l.Scale));
        float textH = PanelText.Height(c, style);
        float rowH = textH + MathF.Max(3f, MathF.Round(2f * l.Scale)) + 2f;
        float width = MathF.Max(MathF.Min(l.ViewportWidth * 0.2f, 96f * l.Scale), MathF.Min(l.ViewportWidth, 130f));
        float x = MathF.Round(6f * l.Scale);
        float y = MathF.Round(l.ViewportHeight * 0.16f);

        float inviteH = party.PendingInviteFrom != null ? (2f * textH) + 3f : 0f;
        float height = (party.Count * rowH) + inviteH + (2f * pad);
        Frame(c, new UiRect(x, y, width, height), Plate);

        float rowY = y + pad;
        for (int n = 0; n < party.Count; n++)
        {
            DrawMember(c, style, party.MemberAt(n), x + pad, rowY, width - (2f * pad), textH, rowH);
            rowY += rowH;
        }

        if (party.PendingInviteFrom is { } inviter)
        {
            DrawInvite(c, style, inviter, x + pad, rowY, width - (2f * pad), textH);
        }
    }

    private static void DrawMember(IUiCanvas c, UiTextStyle style, PartyMemberLine member, float x, float y, float width, float textH, float rowH)
    {
        Span<char> levelMem = stackalloc char[8];
        int levelLength = 0;
        if (PanelText.TryWriteInt(levelMem, Math.Clamp(member.Level, 0, 999), out int written))
        {
            levelLength = written;
        }

        ReadOnlySpan<char> level = levelMem[..levelLength];
        float levelW = c.MeasureText(level, style);
        ReadOnlySpan<char> name = member.Name ?? string.Empty;
        name = name[..PanelText.FitLength(c, name, style, MathF.Max(0f, width - levelW - 6f))];
        UiColor nameColor = member.IsLeader ? LeaderName : MemberName;
        c.Text(name, new Vector2(x + 1f, y + 1f), style, Shadow);
        c.Text(name, new Vector2(x, y), style, nameColor);
        c.Text(level, new Vector2(x + width - levelW + 1f, y + 1f), style, Shadow);
        c.Text(level, new Vector2(x + width - levelW, y), style, Dim);

        float barY = y + textH + 1f;
        float barH = MathF.Max(2f, rowH - textH - 3f);
        UiRect trough = new(x, barY, width, barH);
        c.Solid(trough, Trough);
        int max = Math.Max(1, member.MaxHealth);
        float fraction = Math.Clamp(member.Health, 0, max) / (float)max;
        if (fraction > 0f)
        {
            c.Solid(new UiRect(x, barY, MathF.Max(1f, MathF.Round(width * fraction)), barH), fraction < 0.3f ? BloodLow : Blood);
        }
        else
        {
            c.Solid(new UiRect(x, barY, width, barH), Dead);
        }
    }

    // Two short lines, so the plate can stay narrow: "<name> invites you" and the two commands.
    private static void DrawInvite(IUiCanvas c, UiTextStyle style, string inviter, float x, float y, float width, float textH)
    {
        const string Invites = " invites you";
        float invitesW = c.MeasureText(Invites, style);
        ReadOnlySpan<char> name = inviter;
        name = name[..PanelText.FitLength(c, name, style, MathF.Max(0f, width - invitesW))];
        float nameW = c.Text(name, new Vector2(x, y), style, LeaderName);
        ReadOnlySpan<char> invites = Invites;
        invites = invites[..PanelText.FitLength(c, invites, style, MathF.Max(0f, width - nameW))];
        c.Text(invites, new Vector2(x + nameW, y), style, Dim);

        ReadOnlySpan<char> hint = "/accept or /decline";
        hint = hint[..PanelText.FitLength(c, hint, style, width)];
        c.Text(hint, new Vector2(x, y + textH + 1f), style, Dim);
    }

    private static void Frame(IUiCanvas c, UiRect r, UiColor fill)
    {
        c.Solid(r, fill);
        c.Solid(new UiRect(r.X, r.Y, r.W, 1f), Rim);
        c.Solid(new UiRect(r.X, r.Bottom - 1f, r.W, 1f), Rim);
        c.Solid(new UiRect(r.X, r.Y, 1f, r.H), Rim);
        c.Solid(new UiRect(r.Right - 1f, r.Y, 1f, r.H), Rim);
        c.Solid(new UiRect(r.X + 1f, r.Y + 1f, r.W - 2f, 1f), RimInner);
    }
}
