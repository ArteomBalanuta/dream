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

    public const string NotInPartyText = "You are not in a party.";

    // party: the sender's party members (the sender included) for a Party line, null when the sender is in
    // none. The Command channel is not routed here: the host hands it to its admin commands (M8).
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
                // BH-CLOCK-2. Two live seats can share a CharacterKey and so a display name, so the
                // first match is not the only one: every distinct connection holding the name is a
                // recipient, the same de-duplication by connection the Party case does below. Stopping
                // at the first match dropped the line for the other seat while the sender, who gets an
                // echo either way, saw nothing wrong.
                // The recipients and the sender (the echo) get the same line.
                ServerChatMessage whisper = new(ChatChannel.Whisper, sender.Name, chat.Text, serverTick);
                bool found = false;
                bool senderHeard = false;
                for (int i = 0; i < participants.Count; i++)
                {
                    if (!CharacterNames.Comparer.Equals(participants[i].Name, target))
                    {
                        continue;
                    }

                    bool repeated = false;
                    for (int j = 0; j < i && !repeated; j++)
                    {
                        repeated = CharacterNames.Comparer.Equals(participants[j].Name, target)
                            && string.Equals(participants[j].ConnectionId, participants[i].ConnectionId, StringComparison.Ordinal);
                    }

                    if (repeated)
                    {
                        continue;
                    }

                    found = true;
                    replies.Add(new PendingConnectionReply(participants[i].ConnectionId, whisper));
                    senderHeard |= string.Equals(participants[i].ConnectionId, sender.ConnectionId, StringComparison.Ordinal);
                }

                if (!found)
                {
                    replies.Add(new PendingConnectionReply(
                        sender.ConnectionId,
                        new ServerChatMessage(ChatChannel.System, "", PlayerOfflineText, serverTick)));
                    break;
                }

                if (!senderHeard)
                {
                    replies.Add(new PendingConnectionReply(sender.ConnectionId, whisper));
                }

                break;
            case ChatChannel.Party:
                bool senderListed = false;
                if (party != null)
                {
                    for (int i = 0; i < party.Count && !senderListed; i++)
                    {
                        senderListed = string.Equals(party[i].ConnectionId, sender.ConnectionId, StringComparison.Ordinal);
                    }
                }

                if (party == null || !senderListed)
                {
                    replies.Add(new PendingConnectionReply(
                        sender.ConnectionId,
                        new ServerChatMessage(ChatChannel.System, "", NotInPartyText, serverTick)));
                    break;
                }

                // Members only, each connection once; nobody outside the party hears the line.
                ServerChatMessage partyLine = new(ChatChannel.Party, sender.Name, chat.Text, serverTick);
                for (int i = 0; i < party.Count; i++)
                {
                    bool repeated = false;
                    for (int j = 0; j < i && !repeated; j++)
                    {
                        repeated = string.Equals(party[j].ConnectionId, party[i].ConnectionId, StringComparison.Ordinal);
                    }

                    if (!repeated)
                    {
                        replies.Add(new PendingConnectionReply(party[i].ConnectionId, partyLine));
                    }
                }

                break;
        }
    }
}
