using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Client.Networking;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui.Panels;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Multiplayer;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// M5 (party and trade) end to end, with nothing stubbed on the way: real PlayScenes (no GL) over real TCP
// on a real SQLite database, the shipped enemies and vendor (EnemyContent = ContentCatalog.Shipped,
// SpawnContentNpcs as the production host does). Every intent goes over the wire as the scene sends it and
// every answer comes back the same way, through ServerPartyState, ServerTradeState and the chat lines.
//
// Party: invite, accept, leave and the disconnect of a member; party chat reaches the party only; a real kill
// shares its experience with the nearby mate only; the cube rule follows the flag.
// Trade: a swap of items and gold survives a restart and conserves gold; any change cancels (a move, loot
// gold, an offer edit after a lock, walking off, death, a disconnect, 120 s); a failed write leaves the rows
// whole with both sides paused and nothing duplicated; hostile input is refused.
public sealed class M5EndToEndLoopbackTests
{
    private const string SessionA = "m5e2e-a";
    private const string SessionB = "m5e2e-b";
    private const string SessionC = "m5e2e-c";
    private const string Band = "gear.iron-band";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly Vector3 Pen = new(45f, 1f, 45f);
    private static readonly Vector3 SpotA = new(20f, 1.5f, 20f);
    private static readonly Vector3 SpotB = new(22f, 1.5f, 20f);
    private static readonly Vector3 SpotC = new(52f, 1.5f, 20f);

    private static string KeyOf(string session) => WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");

    private static string NameOf(string session) => $"Name {session}";

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        // A new profile-backed character is created at Strength 10, so player.min_damage
        // (5 + strength * 2) is 25 and max_damage (10 + strength * 4) 50: the baseline's 25-50 per hit,
        // which is what these tests were written for (a wolf, a dire wolf or a cube takes several swings).
        // The seed is therefore a no-op on a new character now, kept because it states the stats the tests
        // assume. A row that already exists (a restart) is left as the earlier phase stored it.
        ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null));
        TestStatBaseline.SeedMissingProfiles(bundle.Schema, KeyOf(SessionA), KeyOf(SessionB), KeyOf(SessionC));
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(bundle);
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        server.Runtime.SpawnContentNpcs();
        return server;
    }

    private static IAuthoritativePlayerTarget Target(WireLoopbackServer server, string session) =>
        WaveIRuntimeKit.Target(server.Runtime, session);

    private static PartyHudState PartyHud(HeadlessPlayScene client) =>
        (PartyHudState)typeof(PlayScene).GetField("_partyHud", Fields)!.GetValue(client.Scene)!;

    private static TradeHudState TradeHud(HeadlessPlayScene client) =>
        (TradeHudState)typeof(PlayScene).GetField("_tradeHud", Fields)!.GetValue(client.Scene)!;

    private static HudIntentQueue HudIntents(HeadlessPlayScene client) =>
        (HudIntentQueue)typeof(PlayScene).GetField("_hudIntents", Fields)!.GetValue(client.Scene)!;

    private static ChatHudState ChatState(HeadlessPlayScene client) =>
        ((ChatInput)typeof(PlayScene).GetField("_chatInput", Fields)!.GetValue(client.Scene)!).State;

    private static bool InventoryVisible(HeadlessPlayScene client) =>
        ((Dream.Application.GameSettings)typeof(PlayScene).GetField("_settings", Fields)!.GetValue(client.Scene)!).InventoryVisible;

    private static string[] ChatLines(HeadlessPlayScene client)
    {
        ChatHudState state = ChatState(client);
        string[] lines = new string[state.LineCount];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = state.GetLine(i).Text;
        }

        return lines;
    }

    private static ClientActionSequenceTracker Tracker(HeadlessPlayScene client) =>
        (ClientActionSequenceTracker)typeof(PlayScene).GetField("_actionSequences", Fields)!.GetValue(client.Scene)!;

    private static ulong CommandSequenceOf(HeadlessPlayScene client) =>
        (ulong)typeof(HeadlessPlayScene).GetField("_commandSequence", Fields)!.GetValue(client)!;

    // ---- stepping ----

    // One command per scene (the attacker's holds the attack), one tick, and every scene has applied that
    // tick's snapshot. Enemies are kept out of the way, except the one given.
    private static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene? attacker = null, EnemyActor? except = null)
    {
        foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
        {
            if (!ReferenceEquals(enemy, except))
            {
                enemy.SetPosition(Pen);
            }
        }

        foreach (HeadlessPlayScene client in clients)
        {
            await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, ReferenceEquals(client, attacker));
            Tracker(client).ObserveCommandSequence(CommandSequenceOf(client));
        }

        await server.TickAsync();
        foreach (HeadlessPlayScene client in clients)
        {
            Assert.True(await client.FlushAsync());
        }

        ulong snapshot = server.Runtime.CurrentServerTick + 1 - (ulong)server.Service.SnapshotIntervalTicks;
        foreach (HeadlessPlayScene client in clients)
        {
            await client.PumpToSnapshotAsync(snapshot);
        }
    }

    // An inviter makes one invite per PartyRules.InviteCooldownTicks: waits it out, the enemies kept in their pen.
    private static async Task InviteCooldownAsync(WireLoopbackServer server)
    {
        for (int i = 0; i < PartyRules.InviteCooldownTicks + 8; i++)
        {
            if (i % 32 == 0)
            {
                foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
                {
                    enemy.SetPosition(Pen);
                }
            }

            await server.TickAsync();
        }
    }

    private static async Task StepUntilAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, Func<bool> done, string what)
    {
        for (int i = 0; i < 60; i++)
        {
            foreach (HeadlessPlayScene client in clients)
            {
                client.Pump();
            }

            if (done())
            {
                return;
            }

            await StepAsync(server, clients);
        }

        foreach (HeadlessPlayScene client in clients)
        {
            client.Pump();
        }

        Assert.True(done(), $"never reached: {what}");
    }

    // Sends the action as the scene and steps until its result reached the scene.
    private static async Task<ServerActionMessage> ActAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, IPlayerActionPayload payload)
    {
        ulong sequence = await who.SendActionAsync(payload);
        for (int i = 0; i < 12; i++)
        {
            await StepAsync(server, clients);
            who.Pump();
            if (who.Applied.OfType<ServerActionMessage>().FirstOrDefault(m => m.Result.ActionSequence == sequence && m.Result.SessionId == who.SessionId) is { } result)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"no result for {who.SessionId}'s action {sequence}");
    }

    private static async Task<ServerActionMessage> AcceptedAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, IPlayerActionPayload payload)
    {
        ServerActionMessage result = await ActAsync(server, clients, who, payload);
        Assert.True(result.Result.Accepted, result.Result.Rejection?.Reason);
        return result;
    }

    private static async Task RejectedAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, IPlayerActionPayload payload, string reason)
    {
        ServerActionMessage result = await ActAsync(server, clients, who, payload);
        Assert.False(result.Result.Accepted, $"{payload.GetType().Name} was accepted");
        Assert.Contains(reason, result.Result.Rejection!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PlaceAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, Vector3 position)
    {
        Target(server, who.SessionId).SetWorldPosition(position);
        who.World.Player.SetWorldPosition(position);
        await StepAsync(server, clients);
        await StepAsync(server, clients);
    }

    private static async Task<HeadlessPlayScene[]> JoinAsync(WireLoopbackServer server, params string[] sessions)
    {
        List<HeadlessPlayScene> joined = [];
        foreach (string session in sessions)
        {
            joined.Add(await HeadlessPlayScene.JoinAsync(server, session));
        }

        HeadlessPlayScene[] all = [.. joined];
        await StepAsync(server, all);
        Vector3[] spots = [SpotA, SpotB, SpotC];
        for (int i = 0; i < all.Length; i++)
        {
            await PlaceAsync(server, all, all[i], spots[i]);
        }

        return all;
    }

    // Awaited, not blocked: see Dream.Tests.Support.PersistenceFlush.
    private static async Task FlushAsync(WireLoopbackServer server, bool allowFailures = false)
    {
        await PersistenceFlush.AwaitAsync(server.Runtime.PersistenceWrites);
        if (!allowFailures)
        {
            Assert.Equal(0, server.Runtime.PersistenceWrites!.FailedWrites);
        }
    }

    private static long Count(MigrationTestDatabase db, string sql) => Convert.ToInt64(db.Scalar(sql));

    private static PlayerInventorySnapshot InventoryOf(WireLoopbackServer server, string session) =>
        server.Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(session))!.Value;

    // A new 1x1 rolled band in the bag of the session, pushed to its scene; its id.
    private static async Task<long> GrantAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, string session, int seed)
    {
        Assert.Equal(
            InventoryEquipOutcome.Equipped,
            server.Runtime.InventoryService.TryGrantRolled(KeyOf(session), Band, ItemRarity.Common, new ItemRollSeed(9, 9, 9, 9, seed)));
        long id = InventoryOf(server, session).AllItems().Max(item => item.ItemInstanceId);
        server.Runtime.MarkInventoryPush(new PlayerSessionId(session));
        await StepAsync(server, clients);
        return id;
    }

    private static async Task GoldAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, string session, long gold)
    {
        Assert.True(server.Runtime.InventoryService.TryAddGold(KeyOf(session), gold));
        server.Runtime.MarkInventoryPush(new PlayerSessionId(session));
        await StepAsync(server, clients);
    }

    // ---- a trade, driven over the wire ----

    private static async Task OpenTradeAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene a, HeadlessPlayScene b)
    {
        await AcceptedAsync(server, clients, a, new TradeRequestIntent(NameOf(b.SessionId)));
        await StepUntilAsync(
            server, clients,
            () => TradeHud(a).Stage == TradeHudStage.Requested && TradeHud(b).Stage == TradeHudStage.Incoming,
            "the request on both screens");
        Assert.Equal(NameOf(b.SessionId), TradeHud(a).Partner);
        Assert.Equal(NameOf(a.SessionId), TradeHud(b).Partner);
        await AcceptedAsync(server, clients, b, new TradeRespondIntent(true));
        await StepUntilAsync(
            server, clients,
            () => TradeHud(a).Stage == TradeHudStage.Open && TradeHud(b).Stage == TradeHudStage.Open,
            "the open trade on both screens");
    }

    private static async Task OfferAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, HeadlessPlayScene other, long[] ids, long gold)
    {
        await AcceptedAsync(server, clients, who, new TradeOfferIntent(ids, gold));
        await StepUntilAsync(
            server, clients,
            () => TradeHud(who).MyCount == ids.Length && TradeHud(who).MyGold == gold
                && TradeHud(other).TheirCount == ids.Length && TradeHud(other).TheirGold == gold,
            "the offer on both screens");
    }

    private static bool Ended(HeadlessPlayScene client, TradeStage stage) =>
        client.Applied.OfType<ServerTradeState>().Any(state => state.Stage == stage);

    private static string? EndReason(HeadlessPlayScene client, TradeStage stage) =>
        client.Applied.OfType<ServerTradeState>().Last(state => state.Stage == stage).EndReason;

    private static async Task BothClosedAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, params HeadlessPlayScene[] who)
    {
        await StepUntilAsync(server, clients, () => who.All(c => !TradeHud(c).IsOpen && Ended(c, TradeStage.Cancelled)), "the trade cancelled");
        Assert.Equal(0, server.Runtime.ActiveTradeCount);
    }

    private static string[] SessionIds(params string[] sessions) => sessions;

    // =====================================================================================
    // Party
    // =====================================================================================

    [Fact]
    public async Task Party_InviteAcceptChatLeave_PartyChatReachesTheMembersOnly_AndADisconnectEndsIt()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB, SessionC);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await using HeadlessPlayScene c = all[2];

        // ---- invite ----
        await RejectedAsync(server, all, a, new PartyInviteIntent("Nobody Here"), "not online");
        await RejectedAsync(server, all, a, new PartyInviteIntent(NameOf(SessionA)), "yourself");
        await RejectedAsync(server, all, a, new PartyInviteIntent(new string('x', 65)), "name is not valid");
        await AcceptedAsync(server, all, a, new PartyInviteIntent(NameOf(SessionB)));
        await StepUntilAsync(server, all, () => PartyHud(b).PendingInviteFrom == NameOf(SessionA), "the invite on B's screen");
        Assert.True(PartyHud(b).IsEmpty == false);
        Assert.Contains(ChatLines(b), line => line.Contains("invited you", StringComparison.Ordinal));
        Assert.True(PartyHud(a).IsEmpty);
        Assert.True(PartyHud(c).IsEmpty);
        // B has an invite pending: nobody else may invite B meanwhile.
        await RejectedAsync(server, all, c, new PartyInviteIntent(NameOf(SessionB)), "already has an invite");

        // ---- accept ----
        await AcceptedAsync(server, all, b, new PartyRespondIntent(true));
        await StepUntilAsync(server, all, () => PartyHud(a).Count == 2 && PartyHud(b).Count == 2, "the party on both screens");
        Assert.Equal(NameOf(SessionA), PartyHud(a).MemberAt(0).Name);
        Assert.True(PartyHud(a).MemberAt(0).IsLeader);
        Assert.Equal(NameOf(SessionB), PartyHud(a).MemberAt(1).Name);
        Assert.False(PartyHud(a).MemberAt(1).IsLeader);
        Assert.Equal(Target(server, SessionB).Combatant.Health.CurrentHealth, PartyHud(a).MemberAt(1).Health);
        Assert.Equal(Target(server, SessionB).Combatant.Health.MaxHealth, PartyHud(a).MemberAt(1).MaxHealth);
        Assert.Null(PartyHud(b).PendingInviteFrom);
        Assert.True(PartyHud(c).IsEmpty);
        Assert.True(server.Runtime.AreInSameParty(KeyOf(SessionA), KeyOf(SessionB)));
        Assert.False(server.Runtime.AreInSameParty(KeyOf(SessionA), KeyOf(SessionC)));

        // Only the leader invites; a member inviting is refused.
        await RejectedAsync(server, all, b, new PartyInviteIntent(NameOf(SessionC)), "leader");
        // C, in no party, cannot answer an invite that does not exist.
        await RejectedAsync(server, all, c, new PartyRespondIntent(true), "no invite");

        // ---- chat: typed as the player would, parsed by the scene's own parser ----
        int cLinesBefore = ChatLines(c).Length;
        Assert.True(ChatInput.TryParse("/p hold the line", out ClientChatMessage? partyLine, out _));
        a.Session.QueueChat(partyLine!);
        await StepUntilAsync(
            server, all,
            () => ChatLines(a).Contains($"[Party] {NameOf(SessionA)}: hold the line") && ChatLines(b).Contains($"[Party] {NameOf(SessionA)}: hold the line"),
            "the party line at both members");
        for (int i = 0; i < 6; i++)
        {
            await StepAsync(server, all);
        }

        Assert.DoesNotContain(c.Applied.OfType<ServerChatMessage>(), m => m.Channel == ChatChannel.Party);
        Assert.Equal(cLinesBefore, ChatLines(c).Length);

        // The outsider's party line is told so, and nobody hears it.
        c.Session.QueueChat(new ClientChatMessage(ChatChannel.Party, "let me in"));
        await StepUntilAsync(server, all, () => ChatLines(c).Contains(ChatRouter.NotInPartyText), "the not-in-a-party line");
        Assert.DoesNotContain(a.Applied.OfType<ServerChatMessage>(), m => m.Text == "let me in");
        Assert.DoesNotContain(b.Applied.OfType<ServerChatMessage>(), m => m.Text == "let me in");

        // World chat still reaches everyone.
        a.Session.QueueChat(new ClientChatMessage(ChatChannel.World, "hello all"));
        await StepUntilAsync(server, all, () => c.Applied.OfType<ServerChatMessage>().Any(m => m.Text == "hello all"), "world chat");

        // ---- a declined invite joins nothing ----
        await InviteCooldownAsync(server);
        await AcceptedAsync(server, all, a, new PartyInviteIntent(NameOf(SessionC)));
        await StepUntilAsync(server, all, () => PartyHud(c).PendingInviteFrom == NameOf(SessionA), "C's invite");
        await AcceptedAsync(server, all, c, new PartyRespondIntent(false));
        await StepUntilAsync(server, all, () => PartyHud(c).IsEmpty, "C's invite cleared");
        Assert.False(server.Runtime.AreInSameParty(KeyOf(SessionA), KeyOf(SessionC)));

        // ---- leave: a party of one dissolves ----
        await AcceptedAsync(server, all, b, new PartyLeaveIntent());
        await StepUntilAsync(server, all, () => PartyHud(a).IsEmpty && PartyHud(b).IsEmpty, "the party dissolved on both screens");
        Assert.False(server.Runtime.AreInSameParty(KeyOf(SessionA), KeyOf(SessionB)));
        await RejectedAsync(server, all, b, new PartyLeaveIntent(), "not in a party");

        // ---- a member who disconnects leaves the party ----
        await InviteCooldownAsync(server);
        await AcceptedAsync(server, all, a, new PartyInviteIntent(NameOf(SessionB)));
        await AcceptedAsync(server, all, b, new PartyRespondIntent(true));
        await StepUntilAsync(server, all, () => PartyHud(a).Count == 2, "the party again");
        await b.LeaveAsync();
        HeadlessPlayScene[] rest = [a, c];
        await StepUntilAsync(server, rest, () => PartyHud(a).IsEmpty, "A's party gone after B left");
        Assert.False(server.Runtime.AreInSameParty(KeyOf(SessionA), KeyOf(SessionB)));
    }

    [Fact]
    public async Task Party_SharedXp_NearbyOnly_AndTheTotalEqualsThePool()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB, SessionC);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await using HeadlessPlayScene c = all[2];
        PlayerCombatant combatantA = Target(server, SessionA).Combatant;
        PlayerCombatant combatantB = Target(server, SessionB).Combatant;
        PlayerCombatant combatantC = Target(server, SessionC).Combatant;
        await PlaceAsync(server, all, a, new Vector3(6f, 1.5f, 20f));
        await PlaceAsync(server, all, b, new Vector3(8f, 1.5f, 20f));

        // A leads a party with B (2 m away) and C (about 46 m from where the enemy dies).
        foreach (string mate in new[] { SessionB, SessionC })
        {
            await InviteCooldownAsync(server);
            await AcceptedAsync(server, all, a, new PartyInviteIntent(NameOf(mate)));
            HeadlessPlayScene who = mate == SessionB ? b : c;
            await AcceptedAsync(server, all, who, new PartyRespondIntent(true));
        }

        await StepUntilAsync(server, all, () => PartyHud(a).Count == 3, "a party of three");
        Assert.True(Vector3.Distance(Target(server, SessionA).Position, Target(server, SessionC).Position) > 40f);

        int levelA = combatantA.Level;
        int levelB = combatantB.Level;
        int levelC = combatantC.Level;
        int expA = combatantA.Experience;
        int expB = combatantB.Experience;
        int expC = combatantC.Experience;

        EnemyActor enemy = await KillAsync(server, all, a);
        int pool = CombatFormulaService.Default.EvaluateEnemyExperienceReward(enemy.Level, levelA);

        Assert.Equal((levelA, levelB, levelC), (combatantA.Level, combatantB.Level, combatantC.Level));
        int gainA = combatantA.Experience - expA;
        int gainB = combatantB.Experience - expB;
        int gainC = combatantC.Experience - expC;
        Assert.True(pool >= 2, $"the pool {pool} is too small to split");
        Assert.Equal(pool, gainA + gainB);
        Assert.Equal(0, gainC);
        // The remainder goes to the first in ordinal session order.
        Assert.Equal(pool / 2 + (pool % 2), gainA);
        Assert.Equal(pool / 2, gainB);
    }

    [Fact]
    public async Task Party_WithoutAParty_TheKillerKeepsTheWholePool()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        PlayerCombatant combatantA = Target(server, SessionA).Combatant;
        PlayerCombatant combatantB = Target(server, SessionB).Combatant;
        int levelA = combatantA.Level;
        int expA = combatantA.Experience;
        int expB = combatantB.Experience;

        EnemyActor enemy = await KillAsync(server, all, a);

        Assert.Equal(levelA, combatantA.Level);
        Assert.Equal(CombatFormulaService.Default.EvaluateEnemyExperienceReward(enemy.Level, levelA), combatantA.Experience - expA);
        Assert.Equal(expB, combatantB.Experience);
    }

    // Holds the attack on a 1 HP enemy in front of the killer until it dies: a real attack command, the
    // real resolver. Returns the dead enemy.
    private static async Task<EnemyActor> KillAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene killer)
    {
        IAuthoritativePlayerTarget target = Target(server, killer.SessionId);
        for (int i = 0; i < 96; i++)
        {
            EnemyActor? enemy = server.Runtime.Enemies.ActiveActors.FirstOrDefault(e => e.IsActive && e.Combatant.Health.IsAlive);
            if (enemy != null)
            {
                enemy.SetPosition(target.EyePosition + (target.Facing * 2f) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
                enemy.Combatant.Health.SetAuthoritativeState(1, enemy.Combatant.Health.MaxHealth);
            }

            await StepAsync(server, clients, killer, enemy);
            if (enemy != null && !enemy.Combatant.Health.IsAlive)
            {
                return enemy;
            }
        }

        throw new Xunit.Sdk.XunitException("no enemy could be killed");
    }

    [Fact]
    public async Task Party_CubeAccess_FollowsTheFlag_AndEndsWhenTheMateLeaves()
    {
        using MigrationTestDatabase db = new();
        // The mate's hit must leave the cube standing for the refusal after the mate leaves to mean
        // anything. At a new character's Strength 10 that hit is 25-50 (5 + strength * 2 up to
        // 10 + strength * 4) against a 100 hp cube, which stands; the seed states the baseline explicitly.
        // The rig builds its own bundle, so the tables are created first (EnsureSchema is idempotent).
        db.Schema.EnsureSchema();
        TestStatBaseline.SeedMissingProfiles(db.Schema, M4BRig.KeyOf(M4BRig.SessionA), M4BRig.KeyOf(M4BRig.SessionB));
        await using M4BRig rig = await M4BRig.StartAsync(db);
        WorldSessionRuntime runtime = rig.Runtime;
        Assert.False(runtime.PartyCubeAccess);

        // A places a cube; B stands before it.
        rig.SetMode(rig.A, PlayerInteractionMode.Building);
        await rig.StepAsync();
        int y = (int)MathF.Floor(rig.Target(rig.A).EyePosition.Y);
        AuthoritativeActionResult placed = await rig.UseAsync(rig.A, new BuildUseIntent(new Vector3(20.5f, y + 0.5f, 22.5f)));
        Assert.True(placed.Accepted, placed.Rejection?.Reason);
        rig.SetMode(rig.A, PlayerInteractionMode.Combat);
        await rig.StepAsync();
        await rig.PlaceAsync(rig.B, new Vector3(20.5f, 1.5f, 18.5f));
        const string Refusal = "That belongs to someone else.";

        Task<AuthoritativeActionResult> HitB() => rig.UseAsync(rig.B, new CombatUseIntent(Vector3.Zero, Vector3.UnitZ));

        // A stranger is refused.
        M4BRig.AssertRejected(await HitB(), Refusal);

        // In a party with the flag off: still refused.
        Assert.True((await rig.UseAsync(rig.A, new PartyInviteIntent(NameOf(M4BRig.SessionB)))).Accepted);
        Assert.True((await rig.UseAsync(rig.B, new PartyRespondIntent(true))).Accepted);
        Assert.True(runtime.AreInSameParty(M4BRig.KeyOf(M4BRig.SessionA), M4BRig.KeyOf(M4BRig.SessionB)));
        await rig.StepAsync(100);
        M4BRig.AssertRejected(await HitB(), Refusal);

        // The flag on: the party mate may damage it.
        runtime.PartyCubeAccess = true;
        await rig.StepAsync(100);
        AuthoritativeActionResult hit = await HitB();
        Assert.True(hit.Accepted, hit.Rejection?.Reason);
        Assert.True(Assert.IsType<CombatResolutionResult>(hit.Payload).DamageApplied > 0);

        // The mate leaves: refused again, flag still on.
        Assert.True((await rig.UseAsync(rig.B, new PartyLeaveIntent())).Accepted);
        await rig.StepAsync(100);
        M4BRig.AssertRejected(await HitB(), Refusal);
    }

    // =====================================================================================
    // Trade
    // =====================================================================================

    [Fact]
    public async Task Trade_ItemsAndGoldSwap_SurvivesRestart_AndConservesTheItemsAndTheGold()
    {
        using MigrationTestDatabase db = new();
        long a1;
        long a2;
        long b1;
        long goldA;
        long goldB;

        await using (WireLoopbackServer server = await StartAsync(db))
        {
            HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
            await using HeadlessPlayScene a = all[0];
            await using HeadlessPlayScene b = all[1];
            a1 = await GrantAsync(server, all, SessionA, 1);
            a2 = await GrantAsync(server, all, SessionA, 2);
            b1 = await GrantAsync(server, all, SessionB, 3);
            await GoldAsync(server, all, SessionA, 500);
            await GoldAsync(server, all, SessionB, 70);
            long totalGold = InventoryOf(server, SessionA).Gold + InventoryOf(server, SessionB).Gold;
            int itemsA = InventoryOf(server, SessionA).AllItems().Count();
            int itemsB = InventoryOf(server, SessionB).AllItems().Count();

            await OpenTradeAsync(server, all, a, b);
            await OfferAsync(server, all, a, b, [a1, a2], 200);
            await OfferAsync(server, all, b, a, [b1], 20);

            // The other side sees the real item text, server-built.
            Assert.Equal(2, TradeHud(b).TheirCount);
            Assert.Contains("Iron Band", TradeHud(b).TheirsAt(0).Text, StringComparison.Ordinal);
            Assert.Contains("Strength", TradeHud(b).TheirsAt(0).Text, StringComparison.Ordinal);
            Assert.Equal(200, TradeHud(b).TheirGold);

            // Nothing moves before both locked and confirmed.
            await AcceptedAsync(server, all, a, new TradeLockIntent());
            await StepUntilAsync(server, all, () => TradeHud(a).MyLocked && TradeHud(b).TheirLocked, "A's lock on both screens");
            Assert.Contains(a1, InventoryOf(server, SessionA).AllItems().Select(item => item.ItemInstanceId));
            await AcceptedAsync(server, all, b, new TradeLockIntent());
            await StepUntilAsync(server, all, () => TradeHud(a).TheirLocked && TradeHud(b).MyLocked, "B's lock on both screens");
            await AcceptedAsync(server, all, a, new TradeConfirmIntent());
            await StepUntilAsync(server, all, () => TradeHud(a).IsOpen && TradeHud(b).IsOpen, "still open after one confirm");
            Assert.Contains(a1, InventoryOf(server, SessionA).AllItems().Select(item => item.ItemInstanceId));

            // The second confirm commits.
            ServerActionMessage done = await AcceptedAsync(server, all, b, new TradeConfirmIntent());
            Assert.IsType<InventoryChangedResult>(done.Result.Payload);
            await StepUntilAsync(
                server, all,
                () => Ended(a, TradeStage.Completed) && Ended(b, TradeStage.Completed) && !TradeHud(a).IsOpen && !TradeHud(b).IsOpen,
                "the trade completed on both screens");
            Assert.Equal(0, server.Runtime.ActiveTradeCount);
            Assert.Contains(ChatLines(a), line => line.Contains("Trade complete", StringComparison.Ordinal));

            // The scenes' own inventories, as pushed.
            await StepUntilAsync(
                server, all,
                () => b.Inventory!.Value.AllItems().Any(item => item.ItemInstanceId == a1) && a.Inventory!.Value.AllItems().Any(item => item.ItemInstanceId == b1),
                "the new bags on both screens");
            Assert.DoesNotContain(a.Inventory!.Value.AllItems(), item => item.ItemInstanceId == a1 || item.ItemInstanceId == a2);
            Assert.Contains(b.Inventory!.Value.AllItems(), item => item.ItemInstanceId == a2);
            Assert.Equal(500 - 200 + 20, a.Inventory.Value.Gold);
            Assert.Equal(70 - 20 + 200, b.Inventory!.Value.Gold);

            // The server's memory and the rows agree.
            await FlushAsync(server);
            Assert.Equal(itemsA - 2 + 1, InventoryOf(server, SessionA).AllItems().Count());
            Assert.Equal(itemsB - 1 + 2, InventoryOf(server, SessionB).AllItems().Count());
            Assert.Equal(totalGold, InventoryOf(server, SessionA).Gold + InventoryOf(server, SessionB).Gold);
            Assert.Equal(KeyOf(SessionB), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {a1}")));
            Assert.Equal(KeyOf(SessionB), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {a2}")));
            Assert.Equal(KeyOf(SessionA), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {b1}")));
            Assert.Equal(KeyOf(SessionB), Convert.ToString(db.Scalar($"SELECT owner_player_name FROM item_instances WHERE item_instance_id = {a1}")));
            Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM (SELECT 1 FROM player_inventory_slots GROUP BY world_id, item_instance_id HAVING COUNT(*) > 1)"));
            goldA = Count(db, $"SELECT COALESCE(SUM(gold), 0) FROM player_wallets WHERE player_name = '{KeyOf(SessionA)}'");
            goldB = Count(db, $"SELECT COALESCE(SUM(gold), 0) FROM player_wallets WHERE player_name = '{KeyOf(SessionB)}'");
            Assert.Equal((320L, 250L), (goldA, goldB));

            await a.LeaveAsync();
            await b.LeaveAsync();
            await FlushAsync(server);
        }

        // ---- a restart: each item once, with its new owner, and the gold as traded ----
        SqlitePools.Release(db.Schema);
        await using WireLoopbackServer again = await StartAsync(db);
        HeadlessPlayScene[] back = await JoinAsync(again, SessionA, SessionB);
        await using HeadlessPlayScene backA = back[0];
        await using HeadlessPlayScene backB = back[1];
        long[] idsA = [.. backA.Inventory!.Value.AllItems().Select(item => item.ItemInstanceId)];
        long[] idsB = [.. backB.Inventory!.Value.AllItems().Select(item => item.ItemInstanceId)];
        Assert.Contains(b1, idsA);
        Assert.DoesNotContain(a1, idsA);
        Assert.DoesNotContain(a2, idsA);
        Assert.Contains(a1, idsB);
        Assert.Contains(a2, idsB);
        Assert.DoesNotContain(b1, idsB);
        Assert.Single(idsA.Concat(idsB), id => id == a1);
        Assert.Single(idsA.Concat(idsB), id => id == a2);
        Assert.Single(idsA.Concat(idsB), id => id == b1);
        Assert.Equal((goldA, goldB), (backA.Inventory.Value.Gold, backB.Inventory.Value.Gold));
    }

    [Fact]
    public async Task Trade_AnyChangeCancels_AMove_LootedGold_AnEditAfterALock_WalkingOff_Death_AndADisconnect()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB, SessionC);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await using HeadlessPlayScene c = all[2];
        long a1 = await GrantAsync(server, all, SessionA, 1);
        long a2 = await GrantAsync(server, all, SessionA, 2);
        long b1 = await GrantAsync(server, all, SessionB, 3);
        await GoldAsync(server, all, SessionA, 300);
        HeadlessPlayScene[] pair = [a, b];

        // 1. An item moved by a real inventory action cancels.
        await OpenTradeAsync(server, all, a, b);
        await OfferAsync(server, all, a, b, [a1], 10);
        PlayerInventorySnapshot bag = InventoryOf(server, SessionA);
        int from = bag.AnchorOfId(a2);
        await AcceptedAsync(server, all, a, new InventoryMoveIntent(from, from == 63 ? 62 : 63));
        await BothClosedAsync(server, all, a, b);
        Assert.Contains("inventory changed", EndReason(b, TradeStage.Cancelled), StringComparison.Ordinal);

        // 2. Gold looted by the other side cancels (anything that touches the wallet counts).
        await OpenTradeAsync(server, all, a, b);
        await OfferAsync(server, all, b, a, [b1], 0);
        Assert.True(server.Runtime.InventoryService.TryAddGold(KeyOf(SessionB), 7));
        await BothClosedAsync(server, all, a, b);
        Assert.Equal(0, server.Runtime.ActiveTradeCount);

        // 3. An offer edit after a lock cancels, from either side.
        await OpenTradeAsync(server, all, a, b);
        await OfferAsync(server, all, a, b, [a1], 0);
        await AcceptedAsync(server, all, b, new TradeLockIntent());
        await ActAsync(server, all, a, new TradeOfferIntent([a1, a2], 0));
        await BothClosedAsync(server, all, a, b);
        Assert.Contains("after locking", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);

        // 4. Walking out of range cancels.
        await OpenTradeAsync(server, all, a, b);
        await OfferAsync(server, all, a, b, [a1], 0);
        await PlaceAsync(server, all, b, new Vector3(40f, 1.5f, 20f));
        await BothClosedAsync(server, all, a, b);
        Assert.Contains("moved apart", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);
        await PlaceAsync(server, all, b, SpotB);

        // 5. Death cancels.
        await OpenTradeAsync(server, all, a, b);
        WaveIRuntimeKit.Kill(server.Runtime, SessionB);
        await BothClosedAsync(server, all, a, b);
        Assert.Contains("died", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);
        Target(server, SessionB).Combatant.Health.SetAuthoritativeState(
            Target(server, SessionB).Combatant.Health.MaxHealth,
            Target(server, SessionB).Combatant.Health.MaxHealth);
        await StepAsync(server, all);

        // 6. Cancelling is also a plain action, from either side.
        await OpenTradeAsync(server, all, a, b);
        await AcceptedAsync(server, all, b, new TradeCancelIntent());
        await BothClosedAsync(server, all, a, b);
        Assert.Contains("cancelled", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);
        await RejectedAsync(server, all, b, new TradeCancelIntent(), "not available");

        // 7. A disconnect mid-trade: the partner is told, the trade is gone, the party-less leaver is clean.
        await OpenTradeAsync(server, all, a, b);
        await OfferAsync(server, all, a, b, [a1], 25);
        await b.LeaveAsync();
        HeadlessPlayScene[] rest = [a, c];
        await StepUntilAsync(server, rest, () => !TradeHud(a).IsOpen && Ended(a, TradeStage.Cancelled), "A told that B left");
        Assert.Contains("left the game", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);
        Assert.Equal(0, server.Runtime.ActiveTradeCount);
        Assert.Contains(a1, InventoryOf(server, SessionA).AllItems().Select(item => item.ItemInstanceId));
        Assert.Equal(300, InventoryOf(server, SessionA).Gold);
        await FlushAsync(server);
        Assert.Equal(KeyOf(SessionA), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {a1}")));
        _ = pair;
    }

    [Fact]
    public async Task Trade_LeftAlone_IsCancelledByTheClockAfter120Seconds()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        long a1 = await GrantAsync(server, all, SessionA, 1);
        await OpenTradeAsync(server, all, a, b);
        await OfferAsync(server, all, a, b, [a1], 0);
        ulong createdAround = server.Runtime.CurrentServerTick;

        // Still open a little before the limit: 119 s of server ticks, the enemies kept in their pen.
        int ticks = Dream.Simulation.Multiplayer.TradeRules.TimeoutTicks - 64;
        for (int i = 0; i < ticks; i++)
        {
            if (i % 64 == 0)
            {
                foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
                {
                    enemy.SetPosition(Pen);
                }
            }

            await server.TickAsync();
        }

        a.Pump();
        b.Pump();
        Assert.True(server.Runtime.CurrentServerTick - createdAround >= (ulong)ticks);
        Assert.Equal(1, server.Runtime.ActiveTradeCount);

        // Past the limit it is gone, on both screens.
        for (int i = 0; i < 160; i++)
        {
            if (i % 64 == 0)
            {
                foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
                {
                    enemy.SetPosition(Pen);
                }
            }

            await server.TickAsync();
        }

        await StepUntilAsync(server, all, () => !TradeHud(a).IsOpen && !TradeHud(b).IsOpen, "the timeout on both screens");
        Assert.Contains("timed out", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);
        Assert.Equal(0, server.Runtime.ActiveTradeCount);
        Assert.Contains(a1, InventoryOf(server, SessionA).AllItems().Select(item => item.ItemInstanceId));
    }

    [Fact]
    public async Task Trade_AnIncomingRequest_DoesNotOpenTheInventory_ShowsANotice_AndTheWindowWaitsForTheInventory()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        MethodInfo frame = typeof(PlayScene).GetMethod("UpdateMultiplayerHud", Fields)!;
        Assert.False(InventoryVisible(b));

        await AcceptedAsync(server, all, a, new TradeRequestIntent(NameOf(SessionB)));
        await StepUntilAsync(server, all, () => TradeHud(b).Stage == TradeHudStage.Incoming, "the request on B's screen");

        // B's screen was not taken over; a line says how to answer.
        Assert.False(InventoryVisible(b));
        Assert.Contains(ChatLines(b), line => line.Contains("wants to trade with you. Open your inventory to answer.", StringComparison.Ordinal));
        // The next frames do not decline it either.
        frame.Invoke(b.Scene, null);
        frame.Invoke(b.Scene, null);
        Assert.True(TradeHud(b).IsOpen);
        Assert.Equal(TradeHudStage.Incoming, TradeHud(b).Stage);
        Assert.Equal(0, HudIntents(b).Count);

        // Opening the inventory shows the window (the trade panel draws over an open inventory).
        b.Scene.PressInventoryKey();
        Assert.True(InventoryVisible(b));
        frame.Invoke(b.Scene, null);
        Assert.True(TradeHud(b).IsOpen);
        Assert.Equal(0, HudIntents(b).Count);

        // Closing it again leaves the request pending; answering works from the window.
        b.Scene.PressInventoryKey();
        frame.Invoke(b.Scene, null);
        Assert.True(TradeHud(b).IsOpen);
        await AcceptedAsync(server, all, b, new TradeRespondIntent(true));
        await StepUntilAsync(server, all, () => TradeHud(a).Stage == TradeHudStage.Open && TradeHud(b).Stage == TradeHudStage.Open, "the open trade");
    }

    [Fact]
    public async Task Trade_ARequestNobodyAnswers_IsCancelledAfter20Seconds_NotThe120OfAnOpenTrade()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await AcceptedAsync(server, all, a, new TradeRequestIntent(NameOf(SessionB)));
        await StepUntilAsync(server, all, () => TradeHud(b).Stage == TradeHudStage.Incoming, "the request on B's screen");

        async Task TicksAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                if (i % 64 == 0)
                {
                    foreach (EnemyActor enemy in server.Runtime.Enemies.ActiveActors)
                    {
                        enemy.SetPosition(Pen);
                    }
                }

                await server.TickAsync();
            }
        }

        await TicksAsync(Dream.Simulation.Multiplayer.TradeRules.RequestTimeoutTicks - 150);
        Assert.Equal(1, server.Runtime.ActiveTradeCount);

        await TicksAsync(250);
        await StepUntilAsync(server, all, () => !TradeHud(a).IsOpen && !TradeHud(b).IsOpen, "the request lapsing on both screens");
        Assert.Contains("not answered", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);
        Assert.Equal(0, server.Runtime.ActiveTradeCount);
    }

    [Fact]
    public async Task Party_ARefreshedInviteSendsNothing_AnInviterIsHeldToACooldown_AndAMutedSenderCannotInvite()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB, SessionC);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await using HeadlessPlayScene c = all[2];
        static int Invited(HeadlessPlayScene who) => ChatLines(who).Count(line => line.Contains("invited you", StringComparison.Ordinal));

        await AcceptedAsync(server, all, a, new PartyInviteIntent(NameOf(SessionB)));
        await StepUntilAsync(server, all, () => Invited(b) == 1, "the invite on B's screen");

        // A second invite at once is held back by the inviter's cooldown, even to another player.
        await RejectedAsync(server, all, a, new PartyInviteIntent(NameOf(SessionC)), "wait a moment");

        // After the cooldown the same inviter renews its invite: A is told, B hears nothing new.
        for (int i = 0; i < PartyRules.InviteCooldownTicks + 10; i++)
        {
            await server.TickAsync();
        }

        await AcceptedAsync(server, all, a, new PartyInviteIntent(NameOf(SessionB)));
        await StepUntilAsync(server, all, () => ChatLines(a).Any(line => line.Contains("already has your invite", StringComparison.Ordinal)), "the renewal notice");
        await StepAsync(server, all);
        Assert.Equal(1, Invited(b));

        // A muted account (the host asks its connection's standing) cannot invite.
        server.Runtime.IsSessionMuted = id => id.Value == SessionC;
        await RejectedAsync(server, all, c, new PartyInviteIntent(NameOf(SessionA)), "muted");
        Assert.Contains(ChatLines(c), line => line == "You are muted.");
        Assert.Null(server.Runtime.Parties.PendingInviteFrom(KeyOf(SessionA)));
    }

    [Fact]
    public async Task Trade_TheClientClosingTheInventory_AsksTheServerToCancel()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await OpenTradeAsync(server, all, a, b);
        MethodInfo frame = typeof(PlayScene).GetMethod("UpdateMultiplayerHud", Fields)!;
        // The window opened the inventory under it.
        Assert.True(InventoryVisible(a));

        // The player closes the inventory: the next frames close the window and send the cancel.
        a.Scene.PressInventoryKey();
        frame.Invoke(a.Scene, null);
        Assert.False(TradeHud(a).IsOpen);
        frame.Invoke(a.Scene, null);
        Assert.Equal(0, HudIntents(a).Count);

        await BothClosedAsync(server, all, a, b);
        Assert.Equal(0, server.Runtime.ActiveTradeCount);
    }

    [Fact]
    public async Task Trade_AFailedWrite_RollsBackWhole_PausesBothSides_AndARestartShowsTheOriginalOwnersAndGold()
    {
        foreach (string trigger in new[]
        {
            "CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;",
            "CREATE TRIGGER fail_slots BEFORE INSERT ON player_inventory_slots BEGIN SELECT RAISE(ABORT, 'simulated'); END;",
        })
        {
            using MigrationTestDatabase db = new();
            long a1;
            long b1;
            await using (WireLoopbackServer server = await StartAsync(db))
            {
                HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
                await using HeadlessPlayScene a = all[0];
                await using HeadlessPlayScene b = all[1];
                a1 = await GrantAsync(server, all, SessionA, 1);
                b1 = await GrantAsync(server, all, SessionB, 2);
                await GoldAsync(server, all, SessionA, 100);
                await GoldAsync(server, all, SessionB, 40);
                await FlushAsync(server);

                db.ExecuteScript(trigger);
                await OpenTradeAsync(server, all, a, b);
                await OfferAsync(server, all, a, b, [a1], 30);
                await OfferAsync(server, all, b, a, [b1], 10);
                await AcceptedAsync(server, all, a, new TradeLockIntent());
                await AcceptedAsync(server, all, b, new TradeLockIntent());
                await AcceptedAsync(server, all, a, new TradeConfirmIntent());
                await AcceptedAsync(server, all, b, new TradeConfirmIntent());
                await StepUntilAsync(server, all, () => Ended(a, TradeStage.Completed) && Ended(b, TradeStage.Completed), "the trade ended on both screens");
                await FlushAsync(server, allowFailures: true);

                // The closure failed as a whole: the rows are exactly as they were.
                Assert.Equal(1, server.Runtime.PersistenceWrites!.FailedWrites);
                Assert.Equal(KeyOf(SessionA), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {a1}")));
                Assert.Equal(KeyOf(SessionB), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {b1}")));
                Assert.Equal(KeyOf(SessionA), Convert.ToString(db.Scalar($"SELECT owner_player_name FROM item_instances WHERE item_instance_id = {a1}")));
                Assert.Equal(100, Count(db, $"SELECT gold FROM player_wallets WHERE player_name = '{KeyOf(SessionA)}'"));
                Assert.Equal(40, Count(db, $"SELECT gold FROM player_wallets WHERE player_name = '{KeyOf(SessionB)}'"));
                Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM (SELECT 1 FROM player_inventory_slots GROUP BY world_id, item_instance_id HAVING COUNT(*) > 1)"));

                // Both sides are paused for the rest of the run: a new trade gets as far as an offer and no further.
                await OpenTradeAsync(server, all, a, b);
                ServerActionMessage refused = await ActAsync(server, all, a, new TradeOfferIntent([], 1));
                Assert.False(refused.Result.Accepted);
                Assert.Contains("paused", refused.Result.Rejection!.Reason, StringComparison.Ordinal);
                await BothClosedAsync(server, all, a, b);
                await b.LeaveAsync();
                await a.LeaveAsync();
            }

            // A restart reloads the true rows: the original owners, the original gold, no duplicate.
            SqlitePools.Release(db.Schema);
            db.ExecuteScript("DROP TRIGGER IF EXISTS fail_wallet; DROP TRIGGER IF EXISTS fail_slots;");
            await using WireLoopbackServer again = await StartAsync(db);
            HeadlessPlayScene[] back = await JoinAsync(again, SessionA, SessionB);
            await using HeadlessPlayScene backA = back[0];
            await using HeadlessPlayScene backB = back[1];
            Assert.Contains(a1, backA.Inventory!.Value.AllItems().Select(item => item.ItemInstanceId));
            Assert.DoesNotContain(a1, backB.Inventory!.Value.AllItems().Select(item => item.ItemInstanceId));
            Assert.Contains(b1, backB.Inventory.Value.AllItems().Select(item => item.ItemInstanceId));
            Assert.DoesNotContain(b1, backA.Inventory.Value.AllItems().Select(item => item.ItemInstanceId));
            Assert.Equal((100L, 40L), (backA.Inventory.Value.Gold, backB.Inventory.Value.Gold));
        }
    }

    [Theory]
    [InlineData(SessionA)]
    [InlineData(SessionB)]
    public async Task Trade_APriorFailedWrite_OnEitherSide_RefusesTheOffer_AndEndsTheTrade(string failer)
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await GoldAsync(server, all, SessionA, 50);
        await GoldAsync(server, all, SessionB, 50);
        await FlushAsync(server);

        // One failed gold credit of the chosen player.
        db.ExecuteScript("CREATE TRIGGER fail_wallet BEFORE UPDATE ON player_wallets BEGIN SELECT RAISE(ABORT, 'simulated'); END;");
        Assert.True(server.Runtime.InventoryService.TryAddGold(KeyOf(failer), 1));
        await FlushAsync(server, allowFailures: true);
        Assert.Equal(1, server.Runtime.PersistenceWrites!.FailedWrites);
        db.ExecuteScript("DROP TRIGGER fail_wallet;");
        await StepAsync(server, all);

        // The trade may open; the first offer of either player is refused and ends it.
        foreach (HeadlessPlayScene who in new[] { a, b })
        {
            await OpenTradeAsync(server, all, a, b);
            ServerActionMessage refused = await ActAsync(server, all, who, new TradeOfferIntent([], 5));
            Assert.False(refused.Result.Accepted);
            Assert.Contains("paused", refused.Result.Rejection!.Reason, StringComparison.Ordinal);
            await BothClosedAsync(server, all, a, b);
        }

        Assert.Equal(50, Count(db, $"SELECT gold FROM player_wallets WHERE player_name = '{KeyOf(SessionA)}'"));
        Assert.Equal(50, Count(db, $"SELECT gold FROM player_wallets WHERE player_name = '{KeyOf(SessionB)}'"));
    }

    [Fact]
    public async Task Trade_Hostile_EveryBadRequestIsRefused_AndNothingMoves()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB, SessionC);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        await using HeadlessPlayScene c = all[2];
        long a1 = await GrantAsync(server, all, SessionA, 1);
        long a2 = await GrantAsync(server, all, SessionA, 2);
        long b1 = await GrantAsync(server, all, SessionB, 3);
        await GoldAsync(server, all, SessionA, 100);

        // No trade yet: every trade step is refused.
        await RejectedAsync(server, all, a, new TradeOfferIntent([a1], 0), "not available");
        await RejectedAsync(server, all, a, new TradeLockIntent(), "not available");
        await RejectedAsync(server, all, a, new TradeConfirmIntent(), "not available");
        await RejectedAsync(server, all, b, new TradeRespondIntent(true), "not available");

        // A request: to oneself, to a stranger far away, to nobody, with a long name.
        await RejectedAsync(server, all, a, new TradeRequestIntent(NameOf(SessionA)), "not available");
        await RejectedAsync(server, all, a, new TradeRequestIntent("Nobody Here"), "not available");
        await RejectedAsync(server, all, a, new TradeRequestIntent(new string('y', 65)), "not available");
        await RejectedAsync(server, all, a, new TradeRequestIntent(NameOf(SessionC)), "too far");
        Assert.Equal(0, server.Runtime.ActiveTradeCount);

        // The request waiting for an answer cannot be offered into, locked or confirmed.
        await AcceptedAsync(server, all, a, new TradeRequestIntent(NameOf(SessionB)));
        await RejectedAsync(server, all, a, new TradeOfferIntent([a1], 0), "not available");
        await RejectedAsync(server, all, a, new TradeRequestIntent(NameOf(SessionB)), "not available");
        await RejectedAsync(server, all, c, new TradeRequestIntent(NameOf(SessionB)), "not available");
        await RejectedAsync(server, all, a, new TradeRespondIntent(true), "not available");
        await AcceptedAsync(server, all, b, new TradeRespondIntent(true));
        await StepUntilAsync(server, all, () => TradeHud(a).Stage == TradeHudStage.Open, "open");

        // Offers: a foreign id, an unknown id, a duplicate, a bad id, nine items, bad gold.
        await RejectedAsync(server, all, a, new TradeOfferIntent([b1], 0), "gone");
        await RejectedAsync(server, all, a, new TradeOfferIntent([987_654], 0), "gone");
        await RejectedAsync(server, all, a, new TradeOfferIntent([a1, a1], 0), "not available");
        await RejectedAsync(server, all, a, new TradeOfferIntent([0], 0), "not available");
        await RejectedAsync(server, all, a, new TradeOfferIntent([-5], 0), "not available");
        await RejectedAsync(server, all, a, new TradeOfferIntent([1, 2, 3, 4, 5, 6, 7, 8, 9], 0), "not available");
        await RejectedAsync(server, all, a, new TradeOfferIntent([], -1), "not available");
        await RejectedAsync(server, all, a, new TradeOfferIntent([], InventoryConstants.MaxGold + 1), "not available");
        await RejectedAsync(server, all, a, new TradeOfferIntent([], 101), "not enough gold");
        // A worn item cannot be offered.
        Assert.Equal(InventoryEquipOutcome.Equipped, server.Runtime.InventoryService.TryEquip(KeyOf(SessionA), InventoryOf(server, SessionA).AnchorOfId(a2)));
        server.Runtime.MarkInventoryPush(new PlayerSessionId(SessionA));
        await StepUntilAsync(server, all, () => !TradeHud(a).IsOpen && !TradeHud(b).IsOpen, "equipping under an open trade changes the bag, so it ends");
        await OpenTradeAsync(server, all, a, b);
        await RejectedAsync(server, all, a, new TradeOfferIntent([a2], 0), "gone");

        // Both sides offering the same id: the owner may, the other side may not.
        await OfferAsync(server, all, a, b, [a1], 0);
        await RejectedAsync(server, all, b, new TradeOfferIntent([a1], 0), "gone");

        // Confirm before lock, a lock with nothing offered, the second confirm of one side.
        await RejectedAsync(server, all, a, new TradeConfirmIntent(), "not available");
        await AcceptedAsync(server, all, a, new TradeLockIntent());
        await RejectedAsync(server, all, a, new TradeConfirmIntent(), "not available");
        await AcceptedAsync(server, all, b, new TradeLockIntent());
        await AcceptedAsync(server, all, a, new TradeConfirmIntent());
        await RejectedAsync(server, all, a, new TradeConfirmIntent(), "not available");
        Assert.True(TradeHud(a).IsOpen);
        Assert.Contains(a1, InventoryOf(server, SessionA).AllItems().Select(item => item.ItemInstanceId));
        await AcceptedAsync(server, all, a, new TradeCancelIntent());
        await BothClosedAsync(server, all, a, b);

        // A lock with both offers empty.
        await OpenTradeAsync(server, all, a, b);
        await RejectedAsync(server, all, a, new TradeLockIntent(), "not available");
        await AcceptedAsync(server, all, a, new TradeCancelIntent());
        await BothClosedAsync(server, all, a, b);

        // Nothing moved through all of it.
        await FlushAsync(server);
        Assert.Equal(KeyOf(SessionA), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {a1}")));
        Assert.Equal(KeyOf(SessionB), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {b1}")));
        Assert.Equal(100, Count(db, $"SELECT gold FROM player_wallets WHERE player_name = '{KeyOf(SessionA)}'"));
    }

    [Fact]
    public async Task Trade_ACommitThatDoesNotFit_IsCancelledWhole_NothingMovesAndNothingIsLost()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene[] all = await JoinAsync(server, SessionA, SessionB);
        await using HeadlessPlayScene a = all[0];
        await using HeadlessPlayScene b = all[1];
        long a1 = await GrantAsync(server, all, SessionA, 1);
        server.Runtime.InventoryService.FillBagLeaving(KeyOf(SessionB), freeCells: 0);
        server.Runtime.MarkInventoryPush(new PlayerSessionId(SessionB));
        await StepAsync(server, all);
        await FlushAsync(server);
        int itemsB = InventoryOf(server, SessionB).AllItems().Count();

        await OpenTradeAsync(server, all, a, b);
        await OfferAsync(server, all, a, b, [a1], 0);
        await AcceptedAsync(server, all, a, new TradeLockIntent());
        await AcceptedAsync(server, all, b, new TradeLockIntent());
        await AcceptedAsync(server, all, a, new TradeConfirmIntent());
        ServerActionMessage last = await ActAsync(server, all, b, new TradeConfirmIntent());

        Assert.False(last.Result.Accepted);
        Assert.Contains("backpack is full", last.Result.Rejection!.Reason, StringComparison.Ordinal);
        await BothClosedAsync(server, all, a, b);
        Assert.Contains("backpack is full", EndReason(a, TradeStage.Cancelled), StringComparison.Ordinal);
        Assert.Contains(a1, InventoryOf(server, SessionA).AllItems().Select(item => item.ItemInstanceId));
        Assert.Equal(itemsB, InventoryOf(server, SessionB).AllItems().Count());
        await FlushAsync(server);
        Assert.Equal(KeyOf(SessionA), Convert.ToString(db.Scalar($"SELECT player_name FROM player_inventory_slots WHERE item_instance_id = {a1}")));
    }
}
