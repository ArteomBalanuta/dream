namespace Dream.Multiplayer.Protocol;

// The dev staging command line, shared by the chat input, the capture script and the server. A line is
// "/dev <sub>[; <sub>...]" sent as ChatChannel.Command. The server answers the sender with exactly one
// System line per subcommand, each starting with ReplyPrefix (ErrorPrefix on failure). Plain chat text,
// so no protocol message or version change is involved.
public static class DevCommandText
{
    public const string Command = "/dev";
    public const string ReplyPrefix = "[dev] ";
    public const string ErrorPrefix = "[dev] error: ";
    public const char Separator = ';';
    public const int MaxSubcommands = 8;

    // True for "/dev" or "/dev <anything>" (ASCII case-insensitive, leading whitespace ignored).
    public static bool IsDevLine(string? text)
    {
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> t = text.AsSpan().TrimStart();
        return t.StartsWith(Command, StringComparison.OrdinalIgnoreCase)
            && (t.Length == Command.Length || char.IsWhiteSpace(t[Command.Length]));
    }

    // The subcommands of a dev line: the text after "/dev", split on Separator, each trimmed, empty ones
    // dropped. Null when the line is not a dev line. An empty list means "/dev" alone (the server answers
    // with help, one line). More than MaxSubcommands is the caller's to reject.
    public static IReadOnlyList<string>? SplitSubcommands(string? text)
    {
        if (!IsDevLine(text))
        {
            return null;
        }

        string rest = text!.TrimStart()[Command.Length..];
        List<string> parts = [];
        foreach (string part in rest.Split(Separator))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                parts.Add(trimmed);
            }
        }

        return parts;
    }
}
