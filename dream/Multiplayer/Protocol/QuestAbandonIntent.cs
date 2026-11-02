namespace Dream.Multiplayer.Protocol;

// M6 (protocol v18, quest.abandon). Drop the active quest QuestKey.
public sealed record QuestAbandonIntent(string QuestKey) : IPlayerActionPayload;
