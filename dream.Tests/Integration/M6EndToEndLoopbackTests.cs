using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// M6 end to end, with nothing stubbed on the way: two real PlayScenes (no GL) over real TCP on a real SQLite
// database, the shipped enemies, camps, items, NPCs and quests (EnemyContent = ContentCatalog.Shipped, the NPCs
// spawned as the production host does). A talks to the gate warden with the E key, accepts a quest through the
// HUD intent queue (the path a click takes), kills a real camp wolf with a real attack command, hands in, and is
// paid once: gold, XP, the reward item, the collected torch taken. The quest log survives a restart, a second
// turn-in is refused before and after it, and B's quest is her own.
//
// Action sequences follow the note of M3EndToEndLoopbackTests: the scene's own tracker is driven the way
// Update does.
public sealed class M6EndToEndLoopbackTests
{
    private const string SessionA = "m6e2e-a";
    private const string SessionB = "m6e2e-b";
    private const string SessionC = "m6e2e-c";
    private const string Torch = "tool.torch";
    private const string ManaPotion = "potion.mana";
    private const string MixedQuest = "marked-for-the-dark";
    private const string WolfQuest = "wolf-cull";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly Vector3 Pen = new(45f, 1f, 45f);
    private static readonly Vector3 SpotA = new(20f, 1.5f, 20f);
    internal static readonly int WardenActorId = ReplicatedActorIdRanges.FirstNpcId + 1;
    private static readonly int QuartermasterActorId = ReplicatedActorIdRanges.FirstNpcId;

    internal static string KeyOf(string session) => WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        server.Runtime.SpawnContentNpcs();
        return server;
    }

    internal static IAuthoritativePlayerTarget Target(WireLoopbackServer server, string session) =>
        WaveIRuntimeKit.Target(server.Runtime, session);

    // ---- driving the scene the way PlayScene.Update does ----

    private static ulong CommandSequenceOf(HeadlessPlayScene client) =>
        (ulong)typeof(HeadlessPlayScene).GetField("_commandSequence", Fields)!.GetValue(client)!;

    private static ClientActionSequenceTracker Tracker(HeadlessPlayScene client) =>
        (ClientActionSequenceTracker)typeof(PlayScene).GetField("_actionSequences", Fields)!.GetValue(client.Scene)!;

    private static ulong NextActionSequence(HeadlessPlayScene client) =>
        (ulong)typeof(ClientActionSequenceTracker).GetField("_nextActionSequence", Fields)!.GetValue(Tracker(client))!;

    internal static QuestHudState Hud(HeadlessPlayScene client) =>
        (QuestHudState)typeof(PlayScene).GetField("_questHud", Fields)!.GetValue(client.Scene)!;

    private static KeyboardState Keyboard(params Keys[] down)
    {
        KeyboardState state = (KeyboardState)Activator.CreateInstance(typeof(KeyboardState), nonPublic: true)!;
        MethodInfo set = typeof(KeyboardState).GetMethods(Fields).Single(m => m.Name == "SetKeyState");
        foreach (Keys key in down)
        {
            object[] args = set.GetParameters().Length == 3 ? [key, 0, true] : [key, true];
            set.Invoke(state, args);
        }

        return state;
    }

    // E, as PlayScene.HandleInteractKey sees it. The sequence of the intent it sent, or null.
    private static ulong? PressE(HeadlessPlayScene client)
    {
        MethodInfo handle = typeof(PlayScene).GetMethod("HandleInteractKey", Fields)!;
        ulong before = NextActionSequence(client);
        handle.Invoke(client.Scene, [Keyboard(Keys.E)]);
        handle.Invoke(client.Scene, [Keyboard()]);
        return NextActionSequence(client) > before ? before : null;
    }

    // A click of the dialogue window: the intent goes into the HUD queue, and the scene's frame hook sends it.
    internal static ulong? Click(HeadlessPlayScene client, IPlayerActionPayload payload)
    {
        ulong before = NextActionSequence(client);
        ((HudIntentQueue)typeof(PlayScene).GetField("_hudIntents", Fields)!.GetValue(client.Scene)!).Enqueue(payload);
        Frame(client);
        return NextActionSequence(client) > before ? before : null;
    }

    // The scene's per frame HUD hook (Update does not run in the headless scene).
    internal static void Frame(HeadlessPlayScene client) =>
        typeof(PlayScene).GetMethod("UpdateMultiplayerHud", Fields)!.Invoke(client.Scene, null);

    // The dialogue window opened the inventory under it; the player closes it before the next E (an open
    // inventory takes the gameplay keys).
    private static void CloseInventory(HeadlessPlayScene client)
    {
        object settings = typeof(PlayScene).GetField("_settings", Fields)!.GetValue(client.Scene)!;
        if ((bool)settings.GetType().GetProperty("InventoryVisible", Fields)!.GetValue(settings)!)
        {
            settings.GetType().GetMethod("ToggleInventory", Fields)!.Invoke(settings, null);
        }
    }

    internal static async Task PlaceAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, Vector3 position)
    {
        Target(server, who.SessionId).SetWorldPosition(position);
        who.World.Player.SetWorldPosition(position);
        await StepAsync(server, clients);
        await StepAsync(server, clients);
        Assert.True(Vector3.Distance(who.World.Player.GetWorldPosition(), position) < 1f, $"{who.SessionId} drifted to {who.World.Player.GetWorldPosition()}");
    }

    // Beside the gate warden, past the interact range of the quartermaster.
    internal static Vector3 BesideTheWarden(HeadlessPlayScene client, float dz = 0f)
    {
        Assert.True(client.Npcs.TryGet(WardenActorId, out ClientNpc warden));
        return new Vector3(warden.Position.X + 1.5f, 1.5f, warden.Position.Z + 1.5f + dz);
    }

    internal static async Task StepAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene? attacker = null, EnemyActor? except = null)
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

    private static async Task WaitActionReadAsync(WireLoopbackServer server, HeadlessPlayScene client, ulong sequence)
    {
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!server.Host.TryGetConnectionContext(client.Connection.ConnectionId, out ServerRoutingContext? context)
            || context?.LastActionMessage?.ActionSequence < sequence
            || context?.LastActionMessage == null)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The server never read {client.SessionId}'s action {sequence}.");
            await Task.Delay(1);
        }
    }

    internal static async Task<ServerActionMessage> ResultAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, ulong? sequence)
    {
        Assert.NotNull(sequence);
        await WaitActionReadAsync(server, who, sequence.Value);
        for (int i = 0; i < 8; i++)
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

    internal static void AssertRejected(ServerActionMessage result, string reason)
    {
        Assert.False(result.Result.Accepted, "the action was accepted");
        Assert.Contains(reason, result.Result.Rejection!.Reason, StringComparison.Ordinal);
    }

    internal static void AssertAccepted(ServerActionMessage result) =>
        Assert.True(result.Result.Accepted, result.Result.Rejection?.Reason);

    internal static async Task StepUntilAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, Func<bool> done, string what)
    {
        for (int i = 0; i < 60; i++)
        {
            await StepAsync(server, clients);
            foreach (HeadlessPlayScene client in clients)
            {
                client.Pump();
            }

            if (done())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new Xunit.Sdk.XunitException($"never reached {what}");
    }

    // Holds the attack on a 1 HP enemy of the kind in front of the killer until it dies: a real attack
    // command, the real resolver, real credit.
    internal static async Task KillAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene killer, string kind)
    {
        IAuthoritativePlayerTarget target = Target(server, killer.SessionId);
        for (int i = 0; i < 96; i++)
        {
            EnemyActor? enemy = server.Runtime.Enemies.ActiveActors.FirstOrDefault(e =>
                e.IsActive && e.Combatant.Health.IsAlive && string.Equals(e.EnemyKind, kind, StringComparison.Ordinal));
            if (enemy != null)
            {
                enemy.SetPosition(target.EyePosition + (target.Facing * 2f) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
                enemy.Combatant.Health.SetAuthoritativeState(1, enemy.Combatant.Health.MaxHealth);
            }

            await StepAsync(server, clients, killer, enemy);
            if (enemy != null && !enemy.Combatant.Health.IsAlive)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException($"no {kind} could be killed");
    }

    // Awaited, not blocked: see Dream.Tests.Support.PersistenceFlush.
    internal static async Task FlushAsync(WireLoopbackServer server)
    {
        await PersistenceFlush.AwaitAsync(server.Runtime.PersistenceWrites);
        Assert.Equal(0, server.Runtime.PersistenceWrites!.FailedWrites);
    }

    private static long Count(MigrationTestDatabase db, string sql) => Convert.ToInt64(db.Scalar(sql));

    internal static QuestHudEntry Entry(HeadlessPlayScene client, string key) =>
        Assert.Single(Hud(client).Entries, entry => entry.Key == key);

    internal static QuestHudOffer Offer(HeadlessPlayScene client, string key) =>
        Assert.Single(Hud(client).Offers, offer => offer.Key == key);

    // E at the warden: the dialogue opens.
    internal static async Task OpenDialogueAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who)
    {
        ulong? talk = PressE(who);
        ServerActionMessage opened = await ResultAsync(server, clients, who, talk);
        AssertAccepted(opened);
        NpcDialogueResult dialogue = Assert.IsType<NpcDialogueResult>(opened.Result.Payload);
        Assert.Equal(WardenActorId, dialogue.NpcActorId);
        Assert.True(Hud(who).DialogueOpen);
        Assert.Equal(WardenActorId, Hud(who).NpcActorId);
    }

    [Fact]
    public async Task TheGateWarden_GivesAQuest_ARealKillAndAnItemFinishIt_ThePayoutIsOnce_AndItAllSurvivesARestart()
    {
        using MigrationTestDatabase db = new();
        string keyA = KeyOf(SessionA);
        string keyB = KeyOf(SessionB);
        QuestDefinition mixed = ContentCatalog.Shipped.Quests.Single(q => q.Key == MixedQuest);
        long goldAtTheEnd;
        int manaAtTheEnd;
        int torchesAtTheEnd;
        (int Level, int Experience) progressionAtTheEnd;

        await using (WireLoopbackServer server = await StartAsync(db))
        {
            HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
            HeadlessPlayScene b = await HeadlessPlayScene.JoinAsync(server, SessionB);
            await using HeadlessPlayScene clientA = a;
            await using HeadlessPlayScene clientB = b;
            HeadlessPlayScene[] both = [a, b];
            WorldSessionRuntime runtime = server.Runtime;
            await StepAsync(server, both);

            // The warden is known to both clients, next to the quartermaster, and nobody has a quest yet.
            Assert.Equal(2, a.Npcs.Count);
            await PlaceAsync(server, both, a, BesideTheWarden(a));
            await PlaceAsync(server, both, b, BesideTheWarden(b, dz: 1f));
            Assert.Empty(Hud(a).Entries);
            Assert.Empty(Hud(b).Entries);

            // A bag with exactly the torch the quest wants (the starter kit's is already there).
            while (runtime.InventoryService.CountBagItems(keyA, Torch) < 1)
            {
                Assert.True(runtime.InventoryService.TryAddGold(keyA, 6));
                Assert.Equal(InventoryEquipOutcome.Equipped, runtime.InventoryService.TryBuy(keyA, Torch, 6));
            }

            // ---- E on the warden opens the dialogue, with all four quests on offer ----
            await OpenDialogueAsync(server, both, a);
            Assert.Equal("Gate Warden", Hud(a).NpcName);
            Assert.Equal(ContentCatalog.Shipped.Npcs[1].Greeting, Hud(a).Greeting);
            Assert.Equal(ContentCatalog.Shipped.Quests.Where(q => q.ExistsOn(WorldDefaults.StartupFloorSize)).Select(q => q.Key), Hud(a).Offers.Select(o => o.Key));
            Assert.DoesNotContain(Hud(a).Offers, offer => offer.Key is "ash-on-the-wind" or "the-long-road" or "grave-howl");
            Assert.All(Hud(a).Offers, offer => Assert.Equal(QuestOfferState.Available, offer.State));
            Assert.All(Hud(a).Offers, offer => Assert.False(string.IsNullOrWhiteSpace(offer.RewardText)));
            Assert.False(a.Vendor.IsOpen);

            // ---- hostile requests: unmet, unknown, wrong NPC, out of range ----
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestTurnInIntent(WardenActorId, MixedQuest))), "quest is not available");
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestAcceptIntent(WardenActorId, "no-such-quest"))), "quest is not available");
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestAcceptIntent(QuartermasterActorId, MixedQuest))), "too far away");
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestAcceptIntent(9999, MixedQuest))), "too far away");
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestAbandonIntent(MixedQuest))), "quest is not available");
            Assert.Empty(Hud(a).Entries);

            // ---- accept: the log arrives with both objectives ----
            AssertAccepted(await ResultAsync(server, both, a, Click(a, new QuestAcceptIntent(WardenActorId, MixedQuest))));
            await StepUntilAsync(server, both, () => Hud(a).Entries.Count == 1, "A's quest log");
            QuestHudEntry entry = Entry(a, MixedQuest);
            Assert.False(entry.Completed);
            Assert.False(entry.Ready);
            Assert.Equal(2, entry.Objectives.Length);
            Assert.Equal(new QuestHudObjective("Slay Wolf", 0, 1), entry.Objectives[0]);
            Assert.Equal("Bring Torch", entry.Objectives[1].Text);
            Assert.Equal(1, entry.Objectives[1].Need);
            Assert.Equal(1, entry.Objectives[1].Have);
            Assert.Equal(QuestOfferState.InProgress, Offer(a, MixedQuest).State);
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestAcceptIntent(WardenActorId, MixedQuest))), "quest is not available");

            // Handing in before the kill is refused.
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestTurnInIntent(WardenActorId, MixedQuest))), "quest is not available");

            // ---- B takes a different quest; hers is her own ----
            await OpenDialogueAsync(server, both, b);
            AssertAccepted(await ResultAsync(server, both, b, Click(b, new QuestAcceptIntent(WardenActorId, WolfQuest))));
            await StepUntilAsync(server, both, () => Hud(b).Entries.Count == 1, "B's quest log");
            Assert.Equal(new QuestHudObjective("Slay Wolf", 0, 2), Entry(b, WolfQuest).Objectives[0]);
            Assert.Single(Hud(a).Entries);

            // ---- a real kill of a camp wolf, far from the warden ----
            await PlaceAsync(server, both, a, SpotA);
            Frame(a);
            Assert.False(Hud(a).DialogueOpen);
            CloseInventory(a);
            await KillAsync(server, both, a, "Wolf");
            await StepUntilAsync(server, both, () => Entry(a, MixedQuest).Objectives[0].Have == 1, "the kill in A's log");
            Assert.True(Entry(a, MixedQuest).Ready);

            // B did not fight: her count did not move, and the server agrees.
            Assert.Equal(0, Entry(b, WolfQuest).Objectives[0].Have);
            Assert.True(runtime.InventoryService.TryGetQuestRecord(keyB, WolfQuest, out PlayerInventoryService.QuestRecord? recordB));
            Assert.Equal(0, recordB.Kills[0]);
            Assert.False(runtime.InventoryService.TryGetQuestRecord(keyB, MixedQuest, out _));
            Assert.False(runtime.InventoryService.TryGetQuestRecord(keyA, WolfQuest, out _));

            // ---- back at the warden the dialogue says ready; hand in ----
            await PlaceAsync(server, both, a, BesideTheWarden(a));
            await OpenDialogueAsync(server, both, a);
            Assert.Equal(QuestOfferState.ReadyToTurnIn, Offer(a, MixedQuest).State);

            long goldBefore = runtime.InventoryService.GoldOf(keyA);
            int torchesBefore = runtime.InventoryService.CountBagItems(keyA, Torch);
            int manaBefore = runtime.InventoryService.CountBagItems(keyA, ManaPotion);
            PlayerCombatant combatantA = Target(server, SessionA).Combatant;
            (int Level, int Experience) before = (combatantA.Level, combatantA.Experience);

            ServerActionMessage handedIn = await ResultAsync(server, both, a, Click(a, new QuestTurnInIntent(WardenActorId, MixedQuest)));
            AssertAccepted(handedIn);
            Assert.IsType<InventoryChangedResult>(handedIn.Result.Payload);
            await StepUntilAsync(server, both, () => Hud(a).Entries.Count == 1 && Entry(a, MixedQuest).Completed, "the completed quest");

            Assert.Equal(goldBefore + mixed.Rewards.Gold, runtime.InventoryService.GoldOf(keyA));
            Assert.Equal(goldBefore + mixed.Rewards.Gold, a.Inventory!.Value.Gold);
            Assert.Equal(torchesBefore - 1, runtime.InventoryService.CountBagItems(keyA, Torch));
            Assert.Equal(manaBefore + 1, runtime.InventoryService.CountBagItems(keyA, ManaPotion));
            Assert.Equal(manaBefore + 1, a.Inventory.Value.AllItems().Count(item => item.ItemKind == ManaPotion));
            Assert.Equal(QuestOfferState.Completed, Offer(a, MixedQuest).State);
            (int Level, int Experience) after = (combatantA.Level, combatantA.Experience);
            if (after.Level == before.Level)
            {
                Assert.Equal(before.Experience + mixed.Rewards.Xp, after.Experience);
            }
            else
            {
                Assert.True(after.Level > before.Level);
            }

            // ---- and not twice ----
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestTurnInIntent(WardenActorId, MixedQuest))), "quest is not available");
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestAcceptIntent(WardenActorId, MixedQuest))), "quest is not available");
            AssertRejected(await ResultAsync(server, both, a, Click(a, new QuestAbandonIntent(MixedQuest))), "quest is not available");
            Assert.Equal(goldBefore + mixed.Rewards.Gold, runtime.InventoryService.GoldOf(keyA));
            Assert.Equal(after, (combatantA.Level, combatantA.Experience));

            // ---- what the server holds ----
            await FlushAsync(server);
            Assert.Equal(1, Count(db, $"SELECT COUNT(*) FROM player_quests WHERE player_name = '{keyA}' AND quest_key = '{MixedQuest}' AND state = 2"));
            Assert.Equal(1, Count(db, $"SELECT COUNT(*) FROM player_quests WHERE player_name = '{keyB}' AND quest_key = '{WolfQuest}' AND state = 1"));
            Assert.Equal(2, Count(db, "SELECT COUNT(*) FROM player_quests"));
            goldAtTheEnd = goldBefore + mixed.Rewards.Gold;
            manaAtTheEnd = manaBefore + 1;
            torchesAtTheEnd = torchesBefore - 1;
            progressionAtTheEnd = after;

            await a.LeaveAsync();
            await b.LeaveAsync();
            await FlushAsync(server);
        }

        SqlitePools.Release(db.Schema);
        Assert.Equal(goldAtTheEnd, Count(db, $"SELECT COALESCE(SUM(gold), 0) FROM player_wallets WHERE player_name = '{keyA}'"));

        await using (WireLoopbackServer again = await StartAsync(db))
        {
            HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(again, SessionA);
            HeadlessPlayScene b = await HeadlessPlayScene.JoinAsync(again, SessionB);
            await using HeadlessPlayScene clientA = a;
            await using HeadlessPlayScene clientB = b;
            HeadlessPlayScene[] both = [a, b];
            WorldSessionRuntime runtime = again.Runtime;
            await StepUntilAsync(again, both, () => Hud(a).Entries.Count == 1 && Hud(b).Entries.Count == 1, "both quest logs after the restart");

            // A's quest is finished and stays finished; her purse, bag and progress are as they were.
            Assert.True(Entry(a, MixedQuest).Completed);
            Assert.Equal(goldAtTheEnd, a.Inventory!.Value.Gold);
            Assert.Equal(goldAtTheEnd, runtime.InventoryService.GoldOf(keyA));
            Assert.Equal(manaAtTheEnd, runtime.InventoryService.CountBagItems(keyA, ManaPotion));
            Assert.Equal(torchesAtTheEnd, runtime.InventoryService.CountBagItems(keyA, Torch));
            PlayerCombatant combatantA = Target(again, SessionA).Combatant;
            Assert.Equal(progressionAtTheEnd, (combatantA.Level, combatantA.Experience));

            // B's quest is still hers, untouched.
            Assert.False(Entry(b, WolfQuest).Completed);
            Assert.Equal(new QuestHudObjective("Slay Wolf", 0, 2), Entry(b, WolfQuest).Objectives[0]);

            await PlaceAsync(again, both, a, BesideTheWarden(a));
            await OpenDialogueAsync(again, both, a);
            Assert.Equal(QuestOfferState.Completed, Offer(a, MixedQuest).State);
            AssertRejected(await ResultAsync(again, both, a, Click(a, new QuestTurnInIntent(WardenActorId, MixedQuest))), "quest is not available");
            AssertRejected(await ResultAsync(again, both, a, Click(a, new QuestAcceptIntent(WardenActorId, MixedQuest))), "quest is not available");
            Assert.Equal(goldAtTheEnd, runtime.InventoryService.GoldOf(keyA));
            Assert.Equal(manaAtTheEnd, runtime.InventoryService.CountBagItems(keyA, ManaPotion));
            await FlushAsync(again);
            Assert.Equal(2, Count(db, "SELECT COUNT(*) FROM player_quests"));

            await a.LeaveAsync();
            await b.LeaveAsync();
        }
    }

    // A real party (the M5 lookup, not a stub): A kills, the mate beside her gets the kill, the mate far from
    // the death does not.
    [Fact]
    public async Task Quest_PartyMateCreditedNearby()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        HeadlessPlayScene b = await HeadlessPlayScene.JoinAsync(server, SessionB);
        HeadlessPlayScene c = await HeadlessPlayScene.JoinAsync(server, SessionC);
        await using HeadlessPlayScene clientA = a;
        await using HeadlessPlayScene clientB = b;
        await using HeadlessPlayScene clientC = c;
        HeadlessPlayScene[] all = [a, b, c];
        await StepAsync(server, all);

        foreach (HeadlessPlayScene who in all)
        {
            await PlaceAsync(server, all, who, BesideTheWarden(who));
            AssertAccepted(await ResultAsync(server, all, who, Click(who, new QuestAcceptIntent(WardenActorId, WolfQuest))));
        }

        foreach (HeadlessPlayScene mate in new[] { b, c })
        {
            // One invite per inviter cooldown (PartyRules.InviteCooldownTicks).
            for (int i = 0; i < Dream.Simulation.Multiplayer.PartyRules.InviteCooldownTicks + 8; i++)
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

            AssertAccepted(await ResultAsync(server, all, a, Click(a, new PartyInviteIntent($"Name {mate.SessionId}"))));
            AssertAccepted(await ResultAsync(server, all, mate, Click(mate, new PartyRespondIntent(true))));
        }

        await StepUntilAsync(server, all, () => all.All(who => Hud(who).Entries.Count == 1), "three quest logs");
        await PlaceAsync(server, all, a, new Vector3(6f, 1.5f, 20f));
        await PlaceAsync(server, all, b, new Vector3(8f, 1.5f, 20f));
        await PlaceAsync(server, all, c, new Vector3(52f, 1.5f, 20f));

        await KillAsync(server, all, a, "Wolf");
        await StepUntilAsync(server, all, () => Entry(b, WolfQuest).Objectives[0].Have == 1, "the kill in B's log");

        Assert.Equal(1, Entry(a, WolfQuest).Objectives[0].Have);
        Assert.Equal(0, Entry(c, WolfQuest).Objectives[0].Have);
        Assert.True(server.Runtime.InventoryService.TryGetQuestRecord(KeyOf(SessionB), WolfQuest, out PlayerInventoryService.QuestRecord? recordB));
        Assert.Equal(1, recordB.Kills[0]);
        Assert.True(server.Runtime.InventoryService.TryGetQuestRecord(KeyOf(SessionC), WolfQuest, out PlayerInventoryService.QuestRecord? recordC));
        Assert.Equal(0, recordC.Kills[0]);
    }
}
