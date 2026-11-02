namespace Dream.Multiplayer.Protocol;

// M6 (protocol v18, quest.turn-in). Turn QuestKey in at the quest giver NPC NpcActorId.
public sealed record QuestTurnInIntent(int NpcActorId, string QuestKey) : IPlayerActionPayload;
