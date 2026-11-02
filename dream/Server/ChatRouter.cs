using Dream.Multiplayer.Protocol;
using Dream.Simulation.Characters;
using OpenTK.Mathematics;

namespace Dream.Server;

// One joined player as chat sees them: the connection to deliver to, the server's display name and
// the server-side position.
public readonly record struct ChatParticipant(string ConnectionId, string Name, Vector3 Position);

// M1-A. Turns one cleaned chat line into replies. Pure: the host supplies the joined players and
// the tick, and sends what comes back through its pending connection replies.
public static class ChatRouter
{
    public const float SayRangeMeters = 30f;

    public const string PlayerOfflineText = "That player is not online.";

    // party: the sender's party members (the sender included) for a Party line, null when the sender is in
    // none. The Party and Command channels are not routed here yet (M5 workstream A, M8 workstream C).
    public static void Route(
        ChatParticipant sender,
        ClientChatMessage chat,
        IReadOnlyList<ChatParticipant> participants,
        ulong serverTick,
        List<PendingConnectionReply> replies,
        IReadOnlyList<ChatParticipant>? party = null)
    {
        switch (chat.Channel)
        {
            case ChatChannel.World:
                ServerChatMessage world = new(ChatChannel.World, sender.Name, chat.Text, serverTick);
                for (int i = 0; i < participants.Count; i++)
                {
                    replies.Add(new PendingConnectionReply(participants[i].ConnectionId, world));
                }

                break;
            case ChatChannel.Say:
                ServerChatMessage say = new(ChatChannel.Say, sender.Name, chat.Text, serverTick);
                float rangeSquared = SayRangeMeters * SayRangeMeters;
                for (int i = 0; i < participants.Count; i++)
                {
                    ChatParticipant listener = participants[i];
                    bool isSender = string.Equals(listener.ConnectionId, sender.ConnectionId, StringComparison.Ordinal);
                    if (isSender || (listener.Position - sender.Position).LengthSquared <= rangeSquared)
                    {
                        replies.Add(new PendingConnectionReply(listener.ConnectionId, say));
                    }
                }

                break;
            case ChatChannel.Whisper:
                string target = CharacterNames.Normalize(chat.Target ?? "");
                ChatParticipant? recipient = null;
                for (int i = 0; i < participants.Count; i++)
                {
                    if (CharacterNames.Comparer.Equals(participants[i].Name, target))
                    {
                        recipient = participants[i];
                        break;
                    }
                }

                if (recipient is not { } found)
                {
                    replies.Add(new PendingConnectionReply(
                        sender.ConnectionId,
                        new ServerChatMessage(ChatChannel.System, "", PlayerOfflineText, serverTick)));
                    break;
                }

                // The recipient and the sender (the echo) get the same line.
                ServerChatMessage whisper = new(ChatChannel.Whisper, sender.Name, chat.Text, serverTick);
                replies.Add(new PendingConnectionReply(found.ConnectionId, whisper));
                if (!string.Equals(found.ConnectionId, sender.ConnectionId, StringComparison.Ordinal))
                {
                    replies.Add(new PendingConnectionReply(sender.ConnectionId, whisper));
                }

                break;
        }
    }
}
