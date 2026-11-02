using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui.Panels;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.Input;

// Chat text entry. Enter opens the line, Enter sends it, Esc cancels it. While the line is open the
// scene must give gameplay and the global keys no input (Update returns true on those frames).
// `/s text` is Say, `/w name text` is Whisper, `/p text` is Party, anything else is World. The party and trade
// commands (`/invite name`, `/accept`, `/decline`, `/leave`, `/trade name`) are player actions, handed to
// SendAction; `/kick /ban /unban /mute /unmute /announce` go to the server as the Command channel, which
// alone decides whether they are allowed.
public sealed class ChatInput
{
    private readonly ImGuiTextInputQueue _characters = new();

    public ChatInput(ChatHudState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public ChatHudState State { get; }

    // Where the slash commands that are player actions (party and trade) go, set by the scene. Null: they
    // are not sent.
    public Action<IPlayerActionPayload>? SendAction { get; set; }

    public bool IsTyping => State.Typing;

    // Characters from the window's text event; ignored unless the line is open.
    public void QueueTextInput(uint unicode) => _characters.Enqueue(unicode);

    public void Open()
    {
        State.ClearInput();
        _characters.Drain();
        State.Typing = true;
    }

    public void Cancel()
    {
        State.ClearInput();
        State.Typing = false;
    }

    // Closes the line; sends a message through send when the text parses to one.
    public void Submit(Action<ClientChatMessage> send)
    {
        string text = State.Input.ToString();
        State.ClearInput();
        State.Typing = false;
        if (TryParseAction(text, out IPlayerActionPayload? action, out string? actionError))
        {
            if (action is not null)
            {
                SendAction?.Invoke(action);
            }
            else if (actionError is not null)
            {
                State.AddSystemLine(actionError);
            }

            return;
        }

        if (TryParse(text, out ClientChatMessage? message, out string? error))
        {
            send(message!);
        }
        else if (error is not null)
        {
            State.AddSystemLine(error);
        }
    }

    public void TypeCharacter(uint unicode)
    {
        if (!Rune.IsValid((int)Math.Min(unicode, int.MaxValue)))
        {
            return;
        }

        Rune rune = new((int)unicode);
        Span<char> units = stackalloc char[2];
        int n = rune.EncodeToUtf16(units);
        if (n == 1 && !ChatHudState.IsPlain(units[0]))
        {
            return;
        }

        if (State.Input.Length + n > ChatHudState.MaxInputChars)
        {
            return;
        }

        for (int i = 0; i < n; i++)
        {
            State.AppendInput(units[i]);
        }
    }

    // One frame of keys. canOpen is false while Settings is open or no server is connected.
    // Returns true when chat used this frame's keys (the line was open, or just closed), so the
    // caller skips the global keys.
    public bool Update(KeyboardState keyboard, bool canOpen, Action<ClientChatMessage> send)
    {
        State.OnlineVisible = canOpen && keyboard.IsKeyDown(Keys.Tab);
        bool enter = keyboard.IsKeyPressed(Keys.Enter) || keyboard.IsKeyPressed(Keys.KeyPadEnter);
        if (!State.Typing)
        {
            _characters.Drain();
            if (enter && canOpen)
            {
                Open();
                return true;
            }

            return false;
        }

        if (!canOpen || keyboard.IsKeyPressed(Keys.Escape))
        {
            Cancel();
            return true;
        }

        foreach (uint unicode in _characters.Drain())
        {
            TypeCharacter(unicode);
        }

        if (keyboard.IsKeyPressed(Keys.Backspace))
        {
            State.Backspace();
        }

        if (enter)
        {
            Submit(send);
        }

        return true;
    }

    // The party and trade commands. False when the line is not one of them (TryParse then handles it). True
    // with the payload to send, or true with an error (usage hint) and no payload.
    public static bool TryParseAction(string? input, out IPlayerActionPayload? payload, out string? error)
    {
        payload = null;
        error = null;
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0 || text[0] != '/')
        {
            return false;
        }

        int space = text.IndexOf(' ');
        string command = space < 0 ? text : text[..space];
        string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        if (command.Equals("/invite", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/trade", StringComparison.OrdinalIgnoreCase))
        {
            string name = ReadName(rest);
            if (name.Length == 0)
            {
                error = command.Equals("/invite", StringComparison.OrdinalIgnoreCase)
                    ? "Invite whom? /invite <name>"
                    : "Trade with whom? /trade <name>";
                return true;
            }

            payload = command.Equals("/invite", StringComparison.OrdinalIgnoreCase)
                ? new PartyInviteIntent(name)
                : new TradeRequestIntent(name);
            return true;
        }

        if (command.Equals("/accept", StringComparison.OrdinalIgnoreCase))
        {
            payload = new PartyRespondIntent(true);
            return true;
        }

        if (command.Equals("/decline", StringComparison.OrdinalIgnoreCase))
        {
            payload = new PartyRespondIntent(false);
            return true;
        }

        if (command.Equals("/leave", StringComparison.OrdinalIgnoreCase))
        {
            payload = new PartyLeaveIntent();
            return true;
        }

        return false;
    }

    // A name that may hold spaces when quoted: "two words". Unquoted, it ends at the first space.
    private static string ReadName(string rest)
    {
        int closing = rest.Length > 0 && rest[0] == '"' ? rest.IndexOf('"', 1) : -1;
        string name;
        if (closing > 0)
        {
            name = rest[1..closing].Trim();
        }
        else
        {
            int split = rest.IndexOf(' ');
            name = split < 0 ? rest : rest[..split];
        }

        return ChatHudState.Sanitize(name, ChatHudState.MaxNameChars);
    }

    // False with error == null for an empty line; false with an error for a command that is not
    // understood; true with the message otherwise.
    public static bool TryParse(string? input, out ClientChatMessage? message, out string? error)
    {
        message = null;
        error = null;
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text[0] != '/')
        {
            message = new ClientChatMessage(ChatChannel.World, Clamp(text));
            return true;
        }

        int space = text.IndexOf(' ');
        string command = space < 0 ? text : text[..space];
        string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        if (command.Equals("/s", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length == 0)
            {
                error = "Say what? /s <text>";
                return false;
            }

            message = new ClientChatMessage(ChatChannel.Say, Clamp(rest));
            return true;
        }

        if (command.Equals("/p", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length == 0)
            {
                error = "Say what to the party? /p <text>";
                return false;
            }

            message = new ClientChatMessage(ChatChannel.Party, Clamp(rest));
            return true;
        }

        if (IsServerCommand(command))
        {
            // The server alone decides; the whole line travels so it can read the arguments.
            message = new ClientChatMessage(ChatChannel.Command, Clamp(text));
            return true;
        }

        if (command.Equals("/w", StringComparison.OrdinalIgnoreCase))
        {
            // Names may hold spaces: /w "two words" text. Unquoted, the name ends at the first space.
            string name;
            string body;
            int closing = rest.Length > 0 && rest[0] == '"' ? rest.IndexOf('"', 1) : -1;
            if (closing > 0)
            {
                name = rest[1..closing].Trim();
                body = rest[(closing + 1)..].Trim();
            }
            else
            {
                int split = rest.IndexOf(' ');
                name = split < 0 ? rest : rest[..split];
                body = split < 0 ? string.Empty : rest[(split + 1)..].Trim();
            }

            if (name.Length == 0 || body.Length == 0)
            {
                error = "Whisper to whom? /w <name> <text>";
                return false;
            }

            message = new ClientChatMessage(ChatChannel.Whisper, Clamp(body), ChatHudState.Sanitize(name, ChatHudState.MaxNameChars));
            return true;
        }

        error = UnknownCommandText;
        return false;
    }

    public const string UnknownCommandText =
        "Unknown command. Use /s, /w <name>, /p, /invite <name>, /accept, /decline, /leave or /trade <name>.";

    private static bool IsServerCommand(string command)
        => command.Equals("/kick", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/ban", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/unban", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/mute", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/unmute", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/announce", StringComparison.OrdinalIgnoreCase);

    private static string Clamp(string text) => ChatHudState.Sanitize(text, ChatHudState.MaxTextChars);
}
