namespace Dream.Multiplayer.Protocol;

// M6 (protocol v18, quest.accept). Accept QuestKey from the quest giver NPC NpcActorId.
public sealed record QuestAcceptIntent(int NpcActorId, string QuestKey) : IPlayerActionPayload;
