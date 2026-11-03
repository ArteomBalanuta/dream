namespace Dream.Multiplayer.Protocol;

public enum QuestState : byte
{
    Active = 1,
    Completed = 2
}

public readonly record struct QuestObjectiveLine(string Text, int Have, int Need);

public readonly record struct QuestLogEntry(string Key, string Title, QuestState State, IReadOnlyList<QuestObjectiveLine> Objectives);

// M6 (protocol v18). Owner only, unprompted: the whole quest log, at most MaxEntries entries.
public sealed record ServerQuestLog(IReadOnlyList<QuestLogEntry> Entries) : IServerToClientMessage
{
    public const string Kind = "server.quest-log";
    public const int MaxEntries = 32;
    public string MessageKind => Kind;
}
