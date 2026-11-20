using Dream.Multiplayer.Protocol;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ui.Panels;

// What the chat panel shows: the last lines, the line being typed and the Tab list. One instance
// per play scene (ClassicHud.Chat), so nothing carries over from an earlier session. Server text
// is hostile: it is stripped of control and bidirectional characters and clamped here, and the
// panel draws it as plain spans (a '%' or '{' stays literal).
public sealed class ChatHudState
{
    public const int VisibleLines = 8;
    public const int MaxStoredLines = 64;
    public const int MaxTextChars = 256;
    public const int MaxNameChars = Dream.Simulation.Characters.CharacterNames.MaxNewNameLength;
    public const int MaxInputChars = 256;
    public const int MaxOnlineNames = 40;

    // A System message is split on '\n' into at most this many stored lines; the rest is dropped.
    public const int MaxLinesPerMessage = 16;

    // Rows shown while the line is open (Typing), wrapped rows counted, before the room above the bar caps it.
    public const int OpenVisibleRows = 24;

    private readonly List<ChatLine> _lines = new();
    private readonly char[] _input = new char[MaxInputChars];
    private int _inputLength;

    public readonly record struct ChatLine(ChatChannel Channel, string Text);

    public bool Typing { get; set; }

    public bool OnlineVisible { get; set; }

    public List<string> Online { get; } = new();

    public int LineCount => _lines.Count;

    public ChatLine GetLine(int index) => _lines[index];

    public ReadOnlySpan<char> Input => _input.AsSpan(0, _inputLength);

    public void Clear()
    {
        _lines.Clear();
        _inputLength = 0;
        Typing = false;
        OnlineVisible = false;
        Online.Clear();
    }

    public void ClearInput() => _inputLength = 0;

    public bool AppendInput(char c)
    {
        if (_inputLength >= _input.Length)
        {
            return false;
        }

        _input[_inputLength++] = c;
        return true;
    }

    public void Backspace()
    {
        if (_inputLength > 0)
        {
            _inputLength--;
            // Never leave half of a surrogate pair behind.
            if (_inputLength > 0 && char.IsHighSurrogate(_input[_inputLength - 1]))
            {
                _inputLength--;
            }
        }
    }

    public void AddServerMessage(ChatChannel channel, string? from, string? text)
    {
        if (!Enum.IsDefined(channel))
        {
            channel = ChatChannel.System;
        }

        if (channel == ChatChannel.System)
        {
            AddSystemPieces(text);
            return;
        }

        string cleanText = Sanitize(text, MaxTextChars);
        if (cleanText.Length == 0)
        {
            return;
        }

        string cleanFrom = Sanitize(from, MaxNameChars);
        string line = channel switch
        {
            ChatChannel.System => cleanText,
            ChatChannel.Say => $"[Say] {cleanFrom}: {cleanText}",
            ChatChannel.Whisper => $"[Whisper] {cleanFrom}: {cleanText}",
            ChatChannel.Party => $"[Party] {cleanFrom}: {cleanText}",
            _ => $"{cleanFrom}: {cleanText}"
        };
        AddLine(channel, line);
    }

    // One System message is one stored line per '\n' piece (the server's help reply), at most
    // MaxLinesPerMessage of them. Empty pieces add no line, so they count toward a scan cap
    // (MaxPiecesScanned) of their own: a message of mostly newlines is not walked in full.
    // Only System splits: a player's Say, World,
    // Whisper or Party text can never add a line.
    public const int MaxPiecesScanned = MaxLinesPerMessage * 4;

    private void AddSystemPieces(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int start = 0;
        int added = 0;
        int scanned = 0;
        while (start <= text.Length && added < MaxLinesPerMessage && scanned++ < MaxPiecesScanned)
        {
            int end = text.IndexOf('\n', start);
            int stop = end < 0 ? text.Length : end;
            if (stop > start && text[stop - 1] == '\r')
            {
                stop--;
            }

            string piece = Sanitize(text[start..stop], MaxTextChars);
            if (piece.Length > 0)
            {
                AddLine(ChatChannel.System, piece);
                added++;
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }
    }

    // A local line (a parse hint); same clamping.
    public void AddSystemLine(string text) => AddLine(ChatChannel.System, Sanitize(text, MaxTextChars));

    private void AddLine(ChatChannel channel, string text)
    {
        if (_lines.Count >= MaxStoredLines)
        {
            _lines.RemoveAt(0);
        }

        _lines.Add(new ChatLine(channel, text));
    }

    // Drops control characters and the bidirectional controls, then cuts to maxChars. Returns the
    // input itself when it is already clean.
    public static string Sanitize(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int limit = Math.Min(value.Length, maxChars);
        bool clean = value.Length <= maxChars;
        for (int i = 0; clean && i < limit; i++)
        {
            clean = IsPlain(value[i]);
        }

        if (clean)
        {
            return value;
        }

        Span<char> buffer = limit <= 512 ? stackalloc char[limit] : new char[limit];
        int n = 0;
        for (int i = 0; i < value.Length && n < maxChars; i++)
        {
            if (IsPlain(value[i]))
            {
                buffer[n++] = value[i];
            }
        }

        if (n > 0 && char.IsHighSurrogate(buffer[n - 1]))
        {
            n--;
        }

        return new string(buffer[..n]);
    }

    public static bool IsPlain(char c) =>
        c >= 0x20 && c != 0x7F && !(c >= 0x80 && c <= 0x9F)
        && c != 0x061C && c != 0x200E && c != 0x200F
        && !(c >= 0x202A && c <= 0x202E) && !(c >= 0x2066 && c <= 0x2069)
        && c != 0x2028 && c != 0x2029 && c != 0xFEFF;
}

// Bottom-left chat log of the last 8 lines, the typing line, and the Tab list of players seen.
public static class ChatPanel
{
    // Drawn in front of every System line, from the channel and never from the message.
    public const string ServerMark = "[Server] ";

    private static readonly UiColor World = new(0xD8, 0xD2, 0xC4, 255);
    private static readonly UiColor Say = new(0xF5, 0xDD, 0xA5, 255);
    private static readonly UiColor Whisper = new(0xC4, 0x9B, 0xE0, 255);
    private static readonly UiColor System = new(0xF4, 0x66, 0x50, 255);

    // Party lines in the green of old verdigris, server commands in tarnished bronze: both stay muted next to
    // the gold of Say and the violet of Whisper.
    private static readonly UiColor Party = new(0x86, 0xB4, 0x7A, 255);
    private static readonly UiColor Command = new(0xC8, 0x9A, 0x5E, 255);
    private static readonly UiColor Panel = new(0x0B, 0x07, 0x06, 170);
    private static readonly UiColor Rim = new(0x5A, 0x44, 0x2A, 220);
    private static readonly UiColor Prompt = new(0x0B, 0x07, 0x06, 235);
    private static readonly UiColor Shadow = new(0, 0, 0, 200);

    public static UiColor ChannelColor(ChatChannel channel) => channel switch
    {
        ChatChannel.Say => Say,
        ChatChannel.Whisper => Whisper,
        ChatChannel.System => System,
        ChatChannel.Party => Party,
        ChatChannel.Command => Command,
        _ => World
    };

    public static void Build(IUiCanvas c, HudView v, in UiLayout l, ChatHudState? chat)
    {
        if (chat is null)
        {
            return;
        }

        UiTextStyle style = UiTextStyle.Body;
        float pad = MathF.Max(2f, MathF.Round(2.5f * l.Scale));
        // The leading between rows, the rim thickness and the caret all used to be a baked 1 or 2 px,
        // so they stayed hairlines while the glyphs tripled with ui_scale. One scaled hairline now.
        float hair = UiText.ShadowPx(in l);
        float rowH = PanelText.Height(c, style) + hair;
        float width = MathF.Max(MathF.Min(l.ViewportWidth * 0.45f, 150f * l.Scale), MathF.Min(l.ViewportWidth, 200f));
        float x = MathF.Round(6f * l.Scale);
        float bottom = l.Bar.Y - MathF.Round(6f * l.Scale);

        if (chat.Typing)
        {
            // Open: a wider panel that wraps and shows more history; the prompt under it is as wide and at
            // the same x, so the two read as one tall box.
            float openWidth = MathF.Min(l.ViewportWidth - 2f * x, MathF.Max(width, 340f * l.Scale));
            float y = bottom - rowH - 2f * pad;
            Frame(c, new UiRect(x, y, openWidth, rowH + 2f * pad), Prompt, hair);
            DrawInput(c, chat, v, x + pad, y + pad, openWidth - 2f * pad, style, hair, pad);
            bottom = y - hair;

            BuildOpenList(c, chat, l, style, x, bottom, openWidth, pad, rowH, hair);
            if (chat.OnlineVisible)
            {
                BuildOnline(c, chat, l, style, pad, rowH, hair);
            }

            return;
        }

        int count = Math.Min(chat.LineCount, ChatHudState.VisibleLines);
        if (count > 0)
        {
            float h = count * rowH + 2f * pad;
            float top = bottom - h;
            Frame(c, new UiRect(x, top, width, h), Panel, hair);
            int first = chat.LineCount - count;
            for (int k = 0; k < count; k++)
            {
                ChatHudState.ChatLine line = chat.GetLine(first + k);
                UiColor color = ChannelColor(line.Channel);
                float ty = MathF.Round(top + pad + k * rowH);
                float tx = x + pad;
                float avail = width - 2f * pad;
                // Say, Whisper and Party already lead with their channel in brackets; System said
                // nothing, so a quoted player name inside a server line was the only sign that part
                // of it came from a player. The marker is drawn here, from the channel, so no text
                // a player can send ever carries it.
                if (line.Channel == ChatChannel.System)
                {
                    c.Text(ServerMark, new Vector2(tx + hair, ty + hair), style, Shadow);
                    float markW = c.Text(ServerMark, new Vector2(tx, ty), style, color);
                    tx += markW;
                    avail = MathF.Max(0f, avail - markW);
                }

                ReadOnlySpan<char> text = line.Text;
                text = text[..PanelText.FitLength(c, text, style, avail)];
                c.Text(text, new Vector2(tx + hair, ty + hair), style, Shadow);
                c.Text(text, new Vector2(tx, ty), style, color);
            }
        }

        if (chat.OnlineVisible)
        {
            BuildOnline(c, chat, l, style, pad, rowH, hair);
        }
    }

    // The open list: newest line at the bottom, each line word-wrapped to the panel, as many rows as the
    // budget allows. Spans only; nothing here allocates.
    private static void BuildOpenList(IUiCanvas c, ChatHudState chat, in UiLayout l, UiTextStyle style, float x, float bottom, float width, float pad, float rowH, float hair)
    {
        if (chat.LineCount == 0)
        {
            return;
        }

        int budget = Math.Min(ChatHudState.OpenVisibleRows, (int)MathF.Floor((bottom - MathF.Round(6f * l.Scale) - 2f * pad) / rowH));
        budget = Math.Max(1, budget);
        float markW = c.MeasureText(ServerMark, style);

        int used = 0;
        int oldest = chat.LineCount;
        int skip = 0;
        for (int k = chat.LineCount - 1; k >= 0 && used < budget; k--)
        {
            ChatHudState.ChatLine line = chat.GetLine(k);
            float avail = RowWidth(width, pad, line.Channel, markW);
            int rows = CountRows(c, line.Text, style, avail);
            int room = budget - used;
            oldest = k;
            if (rows <= room)
            {
                used += rows;
                skip = 0;
            }
            else
            {
                skip = rows - room;
                used = budget;
            }
        }

        float h = used * rowH + 2f * pad;
        float top = bottom - h;
        Frame(c, new UiRect(x, top, width, h), Panel, hair);
        int row = 0;
        for (int k = oldest; k < chat.LineCount; k++)
        {
            ChatHudState.ChatLine line = chat.GetLine(k);
            UiColor color = ChannelColor(line.Channel);
            bool system = line.Channel == ChatChannel.System;
            float avail = RowWidth(width, pad, line.Channel, markW);
            float tx = x + pad + (system ? markW : 0f);
            ReadOnlySpan<char> text = line.Text;
            int pos = 0;
            int index = 0;
            int firstShown = k == oldest ? skip : 0;
            do
            {
                ReadOnlySpan<char> part = NextRow(c, text, ref pos, style, avail, index > 0);
                if (index >= firstShown)
                {
                    float ty = MathF.Round(top + pad + row * rowH);
                    if (system && index == 0)
                    {
                        c.Text(ServerMark, new Vector2(x + pad + hair, ty + hair), style, Shadow);
                        c.Text(ServerMark, new Vector2(x + pad, ty), style, color);
                    }

                    c.Text(part, new Vector2(tx + hair, ty + hair), style, Shadow);
                    c.Text(part, new Vector2(tx, ty), style, color);
                    row++;
                }

                index++;
            }
            while (pos < text.Length);
        }
    }

    private static float RowWidth(float width, float pad, ChatChannel channel, float markW) =>
        MathF.Max(0f, width - 2f * pad - (channel == ChatChannel.System ? markW : 0f));

    private static int CountRows(IUiCanvas c, ReadOnlySpan<char> text, UiTextStyle style, float avail)
    {
        int pos = 0;
        int rows = 0;
        do
        {
            NextRow(c, text, ref pos, style, avail, rows > 0);
            rows++;
        }
        while (pos < text.Length);

        return rows;
    }

    // The next wrapped row of text from pos, with pos moved past it and past the spaces it broke on. Breaks
    // at the last space that fits; with none, hard-breaks, always taking at least one char.
    // Where a space-less word breaks: at least one char, never between the halves of a surrogate pair.
    private static int HardBreak(ReadOnlySpan<char> rest, int fit)
    {
        int take = Math.Max(1, fit);
        if (take < rest.Length && char.IsHighSurrogate(rest[take - 1]))
        {
            if (take > 1)
            {
                take--;
            }
            else if (char.IsLowSurrogate(rest[1]))
            {
                take = 2;
            }
        }

        return take;
    }

    private static ReadOnlySpan<char> NextRow(IUiCanvas c, ReadOnlySpan<char> text, ref int pos, UiTextStyle style, float avail, bool continuation)
    {
        if (continuation)
        {
            while (pos < text.Length && text[pos] == ' ')
            {
                pos++;
            }
        }

        ReadOnlySpan<char> rest = text[pos..];
        int fit = PanelText.FitLength(c, rest, style, avail);
        int take;
        if (fit >= rest.Length)
        {
            take = rest.Length;
        }
        else
        {
            int space = rest[..Math.Min(fit + 1, rest.Length)].LastIndexOf(' ');
            take = space > 0 ? space : HardBreak(rest, fit);
        }

        ReadOnlySpan<char> row = rest[..take];
        while (row.Length > 1 && row[^1] == ' ')
        {
            row = row[..^1];
        }

        pos += take;
        while (pos < text.Length && text[pos] == ' ')
        {
            pos++;
        }

        return row;
    }

    private static void DrawInput(IUiCanvas c, ChatHudState chat, HudView v, float x, float y, float maxWidth, UiTextStyle style, float hair, float pad)
    {
        ReadOnlySpan<char> input = chat.Input;
        const string Caret = ">";
        // MeasureText goes through the canvas's text scale, so the prompt gap and the scroll below
        // are both in the size the line is actually drawn at.
        float caretW = c.MeasureText(Caret, style) + pad;
        // Show the tail so the end being typed stays in view.
        int start = 0;
        while (start < input.Length && c.MeasureText(input[start..], style) > maxWidth - caretW - pad)
        {
            start++;
        }

        c.Text(Caret, new Vector2(x, y), style, Say);
        float tx = x + caretW;
        float w = c.Text(input[start..], new Vector2(tx, y), style, World);
        if (((int)(v.AnimationSeconds * 2f) & 1) == 0)
        {
            // The caret is as thick as the hairline: a 1 px bar beside 38 px glyphs reads as a scratch.
            c.Solid(new UiRect(MathF.Round(tx + w + hair), y, hair, PanelText.Height(c, style)), World);
        }
    }

    private static void BuildOnline(IUiCanvas c, ChatHudState chat, in UiLayout l, UiTextStyle style, float pad, float rowH, float hair)
    {
        int names = Math.Min(chat.Online.Count, ChatHudState.MaxOnlineNames);
        float width = MathF.Max(MathF.Min(l.ViewportWidth * 0.4f, 110f * l.Scale), MathF.Min(l.ViewportWidth, 180f));
        float h = (names + 1) * rowH + 2f * pad;
        float x = MathF.Round((l.ViewportWidth - width) * 0.5f);
        float y = MathF.Round(l.ViewportHeight * 0.18f);
        Frame(c, new UiRect(x, y, width, h), Panel, hair);

        Span<char> header = stackalloc char[24];
        "Online: ".CopyTo(header);
        int n = 8;
        if (PanelText.TryWriteInt(header[n..], chat.Online.Count, out int written))
        {
            n += written;
        }

        PanelText.Centred(c, header[..n], x + width * 0.5f, y + pad, style, Say);
        for (int k = 0; k < names; k++)
        {
            ReadOnlySpan<char> name = chat.Online[k];
            name = name[..PanelText.FitLength(c, name, style, width - 2f * pad)];
            c.Text(name, new Vector2(x + pad, MathF.Round(y + pad + (k + 1) * rowH)), style, World);
        }
    }

    private static void Frame(IUiCanvas c, UiRect r, UiColor fill, float t)
    {
        c.Solid(r, fill);
        c.Solid(new UiRect(r.X, r.Y, r.W, t), Rim);
        c.Solid(new UiRect(r.X, r.Bottom - t, r.W, t), Rim);
        c.Solid(new UiRect(r.X, r.Y, t, r.H), Rim);
        c.Solid(new UiRect(r.Right - t, r.Y, t, r.H), Rim);
    }
}
