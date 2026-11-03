using System.Globalization;
using System.Text;

namespace Dream.Multiplayer.Protocol;

// M1-A. Validates a client chat line on the receive path and queues the cleaned line on the
// connection's context; DedicatedServerHost hands it to ChatRouter right after routing, under the
// tick lock. The message has no sender field: the server decides who spoke.
//
// Dropped without a reply: a connection that has not joined, a channel outside the enum, the System
// channel, a text or target over its limit, a Whisper with no target, text that is empty once
// control and bidi characters are stripped, and a line past the connection's token bucket.
public sealed class ChatMessageHandler : IServerMessageHandler
{
    public string MessageKind => ClientChatMessage.Kind;

    public void Handle(ServerRoutingContext context, IClientToServerMessage message)
    {
        ClientChatMessage chat = (ClientChatMessage)message;
        if (context.LastJoinRequest == null
            || !Enum.IsDefined(chat.Channel)
            || chat.Channel == ChatChannel.System
            || chat.Text == null
            || chat.Text.Length > ClientChatMessage.MaxTextLength
            || (chat.Target != null && chat.Target.Length > ClientChatMessage.MaxTargetLength))
        {
            return;
        }

        string text = Sanitize(chat.Text).Trim();
        if (text.Length == 0)
        {
            return;
        }

        string? target = null;
        if (chat.Channel == ChatChannel.Whisper)
        {
            target = Sanitize(chat.Target ?? "").Trim();
            if (target.Length == 0)
            {
                return;
            }
        }

        TimeProvider clock = context.Clock ?? TimeProvider.System;
        if (!context.TryConsumeChat(clock.GetTimestamp(), clock.TimestampFrequency))
        {
            return;
        }

        context.PendingChats.Add(new ClientChatMessage(chat.Channel, text, target));
    }

    // Removes Control, Format (which holds the bidi marks, embeddings, overrides and isolates),
    // line and paragraph separators, private use characters and unpaired surrogates.
    public static string Sanitize(string value)
    {
        StringBuilder? builder = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool keep;
            int width = 1;
            if (char.IsHighSurrogate(c))
            {
                keep = i + 1 < value.Length
                    && char.IsLowSurrogate(value[i + 1])
                    && !IsRejected(CharUnicodeInfo.GetUnicodeCategory(value, i));
                width = keep ? 2 : 1;
            }
            else
            {
                keep = !char.IsLowSurrogate(c) && !IsRejected(CharUnicodeInfo.GetUnicodeCategory(c));
            }

            if (keep)
            {
                builder?.Append(value, i, width);
            }
            else
            {
                builder ??= new StringBuilder(value.Length).Append(value, 0, i);
            }

            i += width - 1;
        }

        return builder?.ToString() ?? value;
    }

    private static bool IsRejected(UnicodeCategory category) =>
        category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned;
}
