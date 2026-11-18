using Microsoft.Data.Sqlite;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// The per-character quickbar (M4 workstream A): a character that never bound anything has no stored
// bar (null in the snapshot, the content default on the client); the first bind writes all six slots,
// later binds one. Binds are per character, survive a restart, and an invalid bind changes nothing.
public sealed class QuickbarPersistenceTests
{
    private const string SessionA = "m4a-bar-a";
    private const string SessionB = "m4a-bar-b";

    [Fact]
    public async Task ABind_SurvivesARestart_TheFirstBindMaterialisesTheDefault_AndAnotherCharacterSeesNoBind()
    {
        using MigrationTestDatabase db = new();
        string keyA = M4AKit.KeyOf(SessionA);

        await using (WireLoopbackServer server = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
            await using HeadlessPlayScene b = await M4AKit.JoinAsync(server, SessionB);
            await M4AKit.StepAsync(server, a, b);
            Assert.Null(a.Inventory!.Value.Quickbar);
            Assert.Null(b.Inventory!.Value.Quickbar);

            ServerActionMessage bound = await M4AKit.ActAsync(server, a, new QuickbarBindIntent(3, QuickbarEntryKind.Skill, "skill.bolt"), a, b);
            Assert.True(bound.Result.Accepted, bound.Result.Rejection?.Reason);
            Assert.IsType<InventoryChangedResult>(bound.Result.Payload);

            IReadOnlyList<QuickbarSlotSnapshot> bar = a.Inventory!.Value.Quickbar!;
            Assert.Equal(InventoryConstants.QuickbarSlots, bar.Count);
            Assert.Equal(new QuickbarSlotSnapshot(3, QuickbarEntryKind.Skill, "skill.bolt"), bar[3]);
            // The other five are the content default, materialised.
            IReadOnlyList<QuickbarSlotSnapshot> defaults = ContentCatalog.Shipped.DefaultQuickbar;
            for (int i = 0; i < bar.Count; i++)
            {
                if (i != 3)
                {
                    Assert.Equal(defaults[i].Kind, bar[i].Kind);
                    Assert.Equal(defaults[i].Key, bar[i].Key);
                }
            }

            ServerActionMessage cleared = await M4AKit.ActAsync(server, a, new QuickbarBindIntent(0, QuickbarEntryKind.None, string.Empty), a, b);
            Assert.True(cleared.Result.Accepted, cleared.Result.Rejection?.Reason);
            Assert.Equal(QuickbarEntryKind.None, a.Inventory!.Value.Quickbar![0].Kind);

            // Another character is untouched.
            Assert.Null(b.Inventory!.Value.Quickbar);
            Assert.Null(server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(SessionB))!.Value.Quickbar);

            await a.LeaveAsync();
            await b.LeaveAsync();
            await M4AKit.FlushAsync(server);
            Assert.Equal(6, M4AKit.Count(db, $"SELECT COUNT(*) FROM player_quickbar_slots WHERE player_name = '{keyA}'"));
            Assert.Equal(0, M4AKit.Count(db, $"SELECT COUNT(*) FROM player_quickbar_slots WHERE player_name = '{M4AKit.KeyOf(SessionB)}'"));
        }

        SqlitePools.Release(db.Schema);
        await using (WireLoopbackServer again = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene back = await M4AKit.JoinAsync(again, SessionA);
            await using HeadlessPlayScene other = await M4AKit.JoinAsync(again, SessionB);
            await M4AKit.StepAsync(again, back, other);
            IReadOnlyList<QuickbarSlotSnapshot> bar = back.Inventory!.Value.Quickbar!;
            Assert.Equal(new QuickbarSlotSnapshot(3, QuickbarEntryKind.Skill, "skill.bolt"), bar[3]);
            Assert.Equal(QuickbarEntryKind.None, bar[0].Kind);
            Assert.Null(other.Inventory!.Value.Quickbar);
            await back.LeaveAsync();
            await other.LeaveAsync();
        }
    }

    [Fact]
    public async Task InvalidBinds_AreRefused_AndChangeNothing_AnAllEmptyBarStaysEmpty()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await M4AKit.StepAsync(server, a);

        QuickbarBindIntent[] bad =
        [
            new(6, QuickbarEntryKind.Skill, "skill.bolt"),
            new(-1, QuickbarEntryKind.Skill, "skill.bolt"),
            new(0, QuickbarEntryKind.Skill, "skill.nonexistent"),
            new(0, QuickbarEntryKind.Item, "gear.iron-band"),
            new(0, QuickbarEntryKind.Item, "no.such.item"),
            new(0, QuickbarEntryKind.None, "skill.bolt"),
            new(0, (QuickbarEntryKind)9, "skill.bolt"),
        ];
        foreach (QuickbarBindIntent intent in bad)
        {
            ServerActionMessage result = await M4AKit.ActAsync(server, a, intent, a);
            Assert.False(result.Result.Accepted, $"{intent} was accepted");
        }

        Assert.Null(a.Inventory!.Value.Quickbar);
        await M4AKit.FlushAsync(server);
        Assert.Equal(0, M4AKit.Count(db, "SELECT COUNT(*) FROM player_quickbar_slots"));

        // Clear every slot: the all-empty bar is stored and is not replaced by the default.
        for (int slot = 0; slot < InventoryConstants.QuickbarSlots; slot++)
        {
            ServerActionMessage cleared = await M4AKit.ActAsync(server, a, new QuickbarBindIntent(slot, QuickbarEntryKind.None, string.Empty), a);
            Assert.True(cleared.Result.Accepted, cleared.Result.Rejection?.Reason);
        }

        Assert.All(a.Inventory!.Value.Quickbar!, entry => Assert.Equal(QuickbarEntryKind.None, entry.Kind));
        await a.LeaveAsync();
    }
}
