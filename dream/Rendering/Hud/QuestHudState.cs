using Dream.Multiplayer.Protocol;
using Dream.Rendering.Ui.Panels;

namespace Dream.Rendering.Hud;

// One objective line of the quest tracker: the text the server built, what the player has and what is needed.
public readonly record struct QuestHudObjective(string Text, int Have, int Need);

// One quest of the log, as the tracker shows it.
public sealed class QuestHudEntry(string key, string title, bool completed, QuestHudObjective[] objectives)
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    public bool Completed { get; } = completed;

    public QuestHudObjective[] Objectives { get; } = objectives;

    // Every objective is met: the quest can be handed in.
    public bool Ready
    {
        get
        {
            foreach (QuestHudObjective objective in Objectives)
            {
                if (objective.Have < objective.Need)
                {
                    return false;
                }
            }

            return !Completed;
        }
    }
}

// One quest the open dialogue offers. State follows the quest log while the window stays open.
public sealed class QuestHudOffer(string key, string title, string description, string rewardText, QuestOfferState state)
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public string RewardText { get; } = rewardText;

    public QuestOfferState State { get; set; } = state;
}

// What the quest tracker and the NPC dialogue window show (M6 workstream B). One instance per scene, created
// with a parameterless constructor (the headless play scene fills fields by it). The scene fills the log
// from ServerQuestLog and opens the dialogue from NpcDialogueResult; the panels only read, and move the
// selection. Server text is hostile: it is stripped of control characters and clamped on the way in.
public sealed class QuestHudState
{
    public const int MaxEntries = ServerQuestLog.MaxEntries;
    public const int MaxObjectives = 4;
    public const int MaxOffers = 8;
    public const int MaxTitleChars = 64;
    public const int MaxLineChars = 128;
    public const int MaxDescriptionChars = 400;
    public const int MaxCount = 9999;

    private readonly List<QuestHudEntry> _entries = [];
    private readonly List<QuestHudOffer> _offers = [];

    public IReadOnlyList<QuestHudEntry> Entries => _entries;

    public bool DialogueOpen { get; private set; }

    public int NpcActorId { get; private set; }

    public string NpcName { get; private set; } = string.Empty;

    public string Greeting { get; private set; } = string.Empty;

    public IReadOnlyList<QuestHudOffer> Offers => _offers;

    // Index into Offers, or -1.
    public int SelectedOffer { get; private set; } = -1;

    public void SetLog(IReadOnlyList<QuestLogEntry>? entries)
    {
        _entries.Clear();
        int seen = Math.Min(entries?.Count ?? 0, MaxEntries);
        for (int i = 0; i < seen; i++)
        {
            QuestLogEntry entry = entries![i];
            string key = ChatHudState.Sanitize(entry.Key, 64);
            if (key.Length == 0
                || (entry.State != QuestState.Active && entry.State != QuestState.Completed)
                || _entries.Exists(existing => string.Equals(existing.Key, key, StringComparison.Ordinal)))
            {
                continue;
            }

            int count = Math.Min(entry.Objectives?.Count ?? 0, MaxObjectives);
            QuestHudObjective[] objectives = new QuestHudObjective[count];
            for (int n = 0; n < count; n++)
            {
                QuestObjectiveLine line = entry.Objectives![n];
                int need = Math.Clamp(line.Need, 1, MaxCount);
                objectives[n] = new QuestHudObjective(
                    ChatHudState.Sanitize(line.Text, MaxLineChars),
                    Math.Clamp(line.Have, 0, need),
                    need);
            }

            _entries.Add(new QuestHudEntry(
                key,
                ChatHudState.Sanitize(entry.Title, MaxTitleChars),
                entry.State == QuestState.Completed,
                objectives));
        }

        RefreshOfferStates();
    }

    // Opens the dialogue of an NPC, replacing the one that was open. At most MaxOffers offers are kept; a line
    // with an unknown state or no key is dropped.
    public void OpenDialogue(int npcActorId, string? npcName, string? greeting, IReadOnlyList<QuestOfferLine>? offers)
    {
        DialogueOpen = true;
        NpcActorId = npcActorId;
        NpcName = ChatHudState.Sanitize(npcName, MaxTitleChars);
        Greeting = ChatHudState.Sanitize(greeting, MaxDescriptionChars);
        _offers.Clear();
        int seen = Math.Min(offers?.Count ?? 0, MaxOffers * 2);
        for (int i = 0; i < seen && _offers.Count < MaxOffers; i++)
        {
            QuestOfferLine line = offers![i];
            string key = ChatHudState.Sanitize(line.Key, 64);
            if (key.Length == 0
                || !Enum.IsDefined(line.State)
                || _offers.Exists(existing => string.Equals(existing.Key, key, StringComparison.Ordinal)))
            {
                continue;
            }

            _offers.Add(new QuestHudOffer(
                key,
                ChatHudState.Sanitize(line.Title, MaxTitleChars),
                ChatHudState.Sanitize(line.Description, MaxDescriptionChars),
                ChatHudState.Sanitize(line.RewardText, MaxLineChars),
                line.State));
        }

        SelectedOffer = _offers.Count > 0 ? 0 : -1;
    }

    public void CloseDialogue()
    {
        DialogueOpen = false;
        NpcActorId = 0;
        NpcName = string.Empty;
        Greeting = string.Empty;
        _offers.Clear();
        SelectedOffer = -1;
    }

    // The session is over: nothing of it stays.
    public void Reset()
    {
        _entries.Clear();
        CloseDialogue();
    }

    public void Select(int index)
    {
        if ((uint)index < (uint)_offers.Count)
        {
            SelectedOffer = index;
        }
    }

    // Keeps an open dialogue in step with the log: an accepted quest is in progress, a met one ready, a handed
    // in one done, a dropped one open again.
    private void RefreshOfferStates()
    {
        foreach (QuestHudOffer offer in _offers)
        {
            QuestHudEntry? entry = _entries.Find(existing => string.Equals(existing.Key, offer.Key, StringComparison.Ordinal));
            if (entry == null)
            {
                if (offer.State is QuestOfferState.InProgress or QuestOfferState.ReadyToTurnIn)
                {
                    offer.State = QuestOfferState.Available;
                }

                continue;
            }

            offer.State = entry.Completed
                ? QuestOfferState.Completed
                : entry.Ready ? QuestOfferState.ReadyToTurnIn : QuestOfferState.InProgress;
        }
    }
}
