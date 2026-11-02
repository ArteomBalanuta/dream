using System.Globalization;
using System.Text;

namespace Dream.Server;

public enum AdminCommandKind : byte
{
    Kick,
    Ban,
    Unban,
    Mute,
    Unmute,
    Announce
}

// One parsed admin command. Target is the character name (empty for Announce). Minutes is null for a
// permanent ban or mute. Text is the kick or ban reason, or the announcement.
public readonly record struct AdminCommand(AdminCommandKind Kind, string Target, int? Minutes, string Text);

// M8. Parses the line of a ChatChannel.Command: kick, ban, unban, mute, unmute, announce, with or
// without a leading slash. Pure: it decides nothing about who may run the command (the host does, from
// the role the connection joined with). A name with spaces is written in double quotes.
public static class AdminCommands
{
    // A ban or mute is at most ten years; longer is a typing mistake.
    public const int MaxMinutes = 60 * 24 * 365 * 10;

    public const int MaxTextLength = 200;

    public const int MaxTargetLength = 64;

    public const string Usage =
        "Commands: kick <name> [reason], ban <name> [minutes] [reason], unban <name>, mute <name> [minutes], unmute <name>, announce <text>. Quote names with spaces.";

    public static bool TryParse(string? line, out AdminCommand command, out string error)
    {
        command = default;
        error = Usage;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string text = line.Trim();
        if (text[0] == '/')
        {
            text = text[1..].TrimStart();
        }

        int space = IndexOfWhitespace(text);
        string verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        AdminCommandKind kind;
        switch (verb)
        {
            case "kick": kind = AdminCommandKind.Kick; break;
            case "ban": kind = AdminCommandKind.Ban; break;
            case "unban": kind = AdminCommandKind.Unban; break;
            case "mute": kind = AdminCommandKind.Mute; break;
            case "unmute": kind = AdminCommandKind.Unmute; break;
            case "announce":
                if (rest.Length == 0)
                {
                    error = "Usage: announce <text>";
                    return false;
                }

                if (rest.Length > MaxTextLength)
                {
                    error = $"An announcement is at most {MaxTextLength} characters.";
                    return false;
                }

                command = new AdminCommand(AdminCommandKind.Announce, string.Empty, null, rest);
                return true;
            default:
                error = $"Unknown command. {Usage}";
                return false;
        }

        if (!TryTokenize(rest, out List<string> tokens, out string tokenError))
        {
            error = tokenError;
            return false;
        }

        if (tokens.Count == 0)
        {
            error = $"Usage: {verb} <name>" + UsageTail(kind);
            return false;
        }

        string target = tokens[0];
        if (target.Length > MaxTargetLength)
        {
            error = "That name is too long.";
            return false;
        }

        int index = 1;
        int? minutes = null;
        if (kind is AdminCommandKind.Ban or AdminCommandKind.Mute
            && index < tokens.Count
            && IsAllDigits(tokens[index]))
        {
            if (!int.TryParse(tokens[index], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                || parsed < 1
                || parsed > MaxMinutes)
            {
                error = $"Minutes must be from 1 to {MaxMinutes}.";
                return false;
            }

            minutes = parsed;
            index++;
        }

        string reason = string.Empty;
        if (kind is AdminCommandKind.Kick or AdminCommandKind.Ban)
        {
            reason = string.Join(' ', tokens.Skip(index));
        }
        else if (index < tokens.Count)
        {
            error = $"Usage: {verb} <name>" + UsageTail(kind);
            return false;
        }

        if (reason.Length > MaxTextLength)
        {
            error = $"A reason is at most {MaxTextLength} characters.";
            return false;
        }

        command = new AdminCommand(kind, target, minutes, reason);
        return true;
    }

    private static string UsageTail(AdminCommandKind kind) => kind switch
    {
        AdminCommandKind.Kick => " [reason]",
        AdminCommandKind.Ban => " [minutes] [reason]",
        AdminCommandKind.Mute => " [minutes]",
        _ => string.Empty,
    };

    private static int IndexOfWhitespace(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsAllDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    // Whitespace separates tokens; double quotes group a token with spaces in it.
    private static bool TryTokenize(string text, out List<string> tokens, out string error)
    {
        tokens = [];
        error = string.Empty;
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            StringBuilder token = new();
            if (text[i] == '"')
            {
                i++;
                int close = text.IndexOf('"', i);
                if (close < 0)
                {
                    error = "A quote was opened and never closed.";
                    return false;
                }

                token.Append(text, i, close - i);
                i = close + 1;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    token.Append(text[i++]);
                }
            }

            string value = token.ToString().Trim();
            if (value.Length > 0)
            {
                tokens.Add(value);
            }
        }

        return true;
    }
}
