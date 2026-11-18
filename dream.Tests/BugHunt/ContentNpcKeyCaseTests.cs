using Dream.Simulation.Content;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-CONTENT-4: NPC keys were deduped with StringComparer.Ordinal while every lookup of an NPC key
// folds case - _questsByGiver, QuestsFor and the quest-giver match are all OrdinalIgnoreCase. Two
// case-variant NPCs therefore both passed load and then shared one quest list: each offered and each
// accepted the other's quests, and the per-giver cap did not surface it either, because that folds case
// too. Every other content collection (items, enemies, loot tables, skills) already dedupes
// case-insensitively; NPCs were the exception. The dedupe now uses the comparer the lookups use, so a
// case-variant key is a load failure rather than a silently merged NPC.
public sealed class ContentNpcKeyCaseTests
{
    private static string NpcsKeyed(params string[] keys) =>
        "{\"version\":1,\"npcs\":[" + string.Join(",", keys.Select(key =>
            "{\"key\":\"" + key + "\",\"displayName\":\"N\",\"visualKey\":\"npc.vendor\","
            + "\"offsetX\":1,\"offsetZ\":1,\"stock\":[]}")) + "]}";

    // Two keys differing only in case are now refused, naming the duplicate.
    [Fact]
    public void TwoNpcKeysDifferingOnlyInCase_AreRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => ContentBugHunt.Load(npcsJson: NpcsKeyed("gate-warden", "Gate-Warden"), questsJson: "{\"version\":1,\"quests\":[]}"));

        Assert.Contains(ContentCatalog.NpcsFileName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate npc key", failure.Message, StringComparison.Ordinal);
    }

    // The exact-duplicate case was already refused and still is.
    [Fact]
    public void TwoIdenticalNpcKeys_AreStillRefusedAtLoad()
    {
        ContentValidationException failure = Assert.Throws<ContentValidationException>(
            () => ContentBugHunt.Load(npcsJson: NpcsKeyed("gate-warden", "gate-warden"), questsJson: "{\"version\":1,\"quests\":[]}"));

        Assert.Contains("duplicate npc key", failure.Message, StringComparison.Ordinal);
    }

    // Control: distinct keys still load, including keys that merely share a prefix or differ by more
    // than case, and the shipped content is unaffected.
    [Fact]
    public void DistinctNpcKeys_StillLoad()
    {
        ContentCatalog catalog = ContentBugHunt.Load(
            npcsJson: NpcsKeyed("gate-warden", "gate-warden-two", "spawn-quartermaster"),
            questsJson: "{\"version\":1,\"quests\":[]}");

        Assert.Equal(3, catalog.Npcs.Count);
        Assert.Equal(2, ContentCatalog.Shipped.Npcs.Count);
    }

    // The point of the finding: the dedupe comparer and the quest lookup comparer agree, so a quest list
    // belongs to exactly one authored NPC. With the old Ordinal dedupe both spellings resolved here.
    [Fact]
    public void AQuestGiverLookup_FoldsCase_SoOnlyOneNpcMayOwnThatKey()
    {
        ContentCatalog shipped = ContentCatalog.Shipped;

        Assert.NotEmpty(shipped.QuestsFor("gate-warden"));
        Assert.Equal(shipped.QuestsFor("gate-warden").Count, shipped.QuestsFor("GATE-WARDEN").Count);
    }
}
