namespace Dream.Multiplayer.Protocol;

// M6 (protocol v18). Where a quest stands for the asking player.
public enum QuestOfferState : byte
{
    Available,
    InProgress,
    ReadyToTurnIn,
    Completed
}

public readonly record struct QuestOfferLine(string Key, string Title, string Description, string RewardText, QuestOfferState State);

// M6 (protocol v18, npc.dialogue). The answer to NpcInteractIntent for an NPC that gives quests.
public sealed record NpcDialogueResult(
    int NpcActorId,
    string NpcName,
    string Greeting,
    IReadOnlyList<QuestOfferLine> Quests) : IActionResultPayload;
