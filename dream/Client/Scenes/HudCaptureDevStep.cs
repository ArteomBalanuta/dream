using Dream.Multiplayer.Protocol;

namespace Dream.Client.Scenes;

// The capture script's dev: step: turns "dev:clear;spawn:wolf:2:3" into the chat line
// "/dev clear; spawn wolf 2 3" and recognises the server's replies. Pure, so the grammar is testable.
internal static class HudCaptureDevStep
{
    // Frames to wait for the replies before giving up.
    public const int ReplyTimeoutFrames = 240;

    // Frames to keep idling after the last reply, so the staged world reaches the client.
    public const int SettleAfterReplyFrames = 30;

    private const string StepPrefix = "dev:";

    // False for anything but a well-formed dev step: no subcommand, an empty subcommand, more than
    // DevCommandText.MaxSubcommands, or a line longer than ClientChatMessage.MaxTextLength.
    public static bool TryBuildChatLine(string step, out string line, out int expectedReplies)
    {
        line = string.Empty;
        expectedReplies = 0;
        if (step is null || !step.StartsWith(StepPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] raw = step[StepPrefix.Length..].Split(DevCommandText.Separator);
        List<string> subs = new(raw.Length);
        foreach (string part in raw)
        {
            string sub = string.Join(' ', part.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (sub.Length == 0)
            {
                return false;
            }

            subs.Add(sub);
        }

        string candidate = DevCommandText.Command + " " + string.Join(DevCommandText.Separator + " ", subs);
        IReadOnlyList<string>? split = DevCommandText.SplitSubcommands(candidate);
        if (split is null || split.Count != subs.Count || split.Count == 0 || split.Count > DevCommandText.MaxSubcommands
            || candidate.Length > ClientChatMessage.MaxTextLength)
        {
            return false;
        }

        line = candidate;
        expectedReplies = split.Count;
        return true;
    }

    public static bool IsReply(ChatChannel channel, string text) =>
        channel == ChatChannel.System && text is not null && text.StartsWith(DevCommandText.ReplyPrefix, StringComparison.Ordinal);

    public static string FormatReplyLog(string line, string reply) => $"[hud-capture] dev {line} -> {reply}";

    public static string FormatTimeoutLog(string line, int missing) => $"[hud-capture] dev {line} -> no reply ({missing} missing)";
}
