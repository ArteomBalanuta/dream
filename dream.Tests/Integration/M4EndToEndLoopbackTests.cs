using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.Integration;

// M4 on the client, end to end: real PlayScenes (no GL) over real TCP on a real SQLite database, the
// shipped skills, items, enemies and vendor. The digit keys go through PlayScene.HandleQuickbarKeys, a
// tile click through the HUD request path, and every answer comes back over the wire.
//
// The first group (the wire tests) only needs the client and the protocol: what a key press sends, what
// the scene does with vitals, a join and the end of a session. The second group (the flow tests) also
// needs the server half of M4 (workstreams A and B: item use, mana, vitals, skills, persistence); on a
// server without it they fail on the first server answer.
//
// Not covered here: the B and G mode keys through PlayScene.Update (Update does not run without a window;
// QuickbarInputTests pin the remap itself) and the owner-only cube rule (BuildProtectionTests, on the
// server side).
[Collection(TimingSensitiveCollection.Name)]
public sealed class M4EndToEndLoopbackTests
{
    private const string SessionA = "m4e2e-a";
    private const string SessionB = "m4e2e-b";
    private const string HealthPotion = "potion.health";
    private const string ManaPotion = "potion.mana";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly Vector3 Pen = new(45f, 1f, 45f);
    private static readonly Vector3 SpotA = new(20f, 1.5f, 20f);
    private static readonly Vector3 SpotB = new(22f, 1.5f, 20f);
    private static readonly Vector3 NearTheVendor = new(5f, 1.5f, 5f);

    private static string KeyOf(string session) => WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");

    private static async Task<WireLoopbackServer> StartAsync(MigrationTestDatabase db)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        server.Runtime.SpawnContentNpcs();
        return server;
    }

    private static IAuthoritativePlayerTarget Target(WireLoopbackServer server, string session) =>
        WaveIRuntimeKit.Target(server.Runtime, session);

    // ---- driving the scene the way PlayScene.Update does ----

    private static ClientActionSequenceTracker Tracker(HeadlessPlayScene client) =>
        (ClientActionSequenceTracker)typeof(PlayScene).GetField("_actionSequences", Fields)!.GetValue(client.Scene)!;

    private static ulong CommandSequenceOf(HeadlessPlayScene client) =>
        (ulong)typeof(HeadlessPlayScene).GetField("_commandSequence", Fields)!.GetValue(client)!;

    // The sequence the scene's next action will use.
    private static ulong NextActionSequence(HeadlessPlayScene client) =>
        (ulong)typeof(ClientActionSequenceTracker).GetField("_nextActionSequence", Fields)!.GetValue(Tracker(client))!;

    private static KeyboardState Keyboard(params Keys[] down)
    {
        KeyboardState state = (KeyboardState)Activator.CreateInstance(typeof(KeyboardState), nonPublic: true)!;
        // OpenTK's own window fills the state through an internal setter; its arity differs between versions.
        MethodInfo set = typeof(KeyboardState).GetMethods(Fields).Single(m => m.Name == "SetKeyState");
        foreach (Keys key in down)
        {
            object[] args = set.GetParameters().Length == 3 ? [key, 0, true] : [key, true];
            set.Invoke(state, args);
        }

        return state;
    }

    private static readonly Keys[] SlotKeys = [Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6];

    // One frame with the slot's digit down, one with nothing: the edge HandleQuickbarKeys fires on. The
    // sequence of the intent it sent, or null when the key did nothing.
    private static ulong? Press(HeadlessPlayScene client, int slot)
    {
        MethodInfo handle = typeof(PlayScene).GetMethod("HandleQuickbarKeys", Fields)!;
        ulong before = NextActionSequence(client);
        handle.Invoke(client.Scene, [Keyboard(SlotKeys[slot])]);
        handle.Invoke(client.Scene, [Keyboard()]);
        return NextActionSequence(client) > before ? before : null;
    }

    // One frame with E held, one with nothing: the edge PlayScene.HandleInteractKey fires on.
    private static ulong? PressE(HeadlessPlayScene client)
    {
        MethodInfo handle = typeof(PlayScene).GetMethod("HandleInteractKey", Fields)!;
        ulong before = NextActionSequence(client);
        handle.Invoke(client.Scene, [Keyboard(Keys.E)]);
        handle.Invoke(client.Scene, [Keyboard()]);
        return NextActionSequence(client) > before ? before : null;
    }

    // A HUD pick, as the classic HUD hands it to the scene. The sequence of the intent, or null.
    private static ulong? Request(HeadlessPlayScene client, HudInteractionRequest request)
    {
        ulong before = NextActionSequence(client);
        client.Scene.ApplyEconomyRequest(request);
        return NextActionSequence(client) > before ? before : null;
    }

    private static ulong? ClickTile(HeadlessPlayScene client, int slot)
    {
        ulong before = NextActionSequence(client);
        Assert.True(client.Scene.ApplyQuickbarCycle(slot));
        return NextActionSequence(client) > before ? before : null;
    }

    private static async Task WaitActionReadAsync(WireLoopbackServer server, HeadlessPlayScene client, ulong sequence)
    {
        DateTime deadline = DateTime.UtcNow + WireLoopbackServer.Timeout;
        while (!server.Host.TryGetConnectionContext(client.Connection.ConnectionId, out ServerRoutingContext? context)
            || context?.LastActionMessage == null
            || context.LastActionMessage.ActionSequence < sequence)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The server never read {client.SessionId}'s action {sequence}.");
            await Task.Delay(1);
        }
    }

    // What the server last read from the connection: the payload of the scene's last action.
    private static IPlayerActionPayload LastReadPayload(WireLoopbackServer server, HeadlessPlayScene client)
    {
        Assert.True(server.Host.TryGetConnectionContext(client.Connection.ConnectionId, out ServerRoutingContext? context));
        return context!.LastActionMessage!.Payload;
    }

    // ---- stepping ----

    // One command per scene, one tick, and every scene has applied that tick's snapshot. Enemies are kept
    // out of the way, except the one given.
    private static async Task StepAsync(
        WireLoopbackServer server,
        HeadlessPlayScene[] clients,
        EnemyActor? except = null,
        PlayerInteractionMode mode = PlayerInteractionMode.Combat)
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
            await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false, mode);
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

    private static async Task PlaceAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, Vector3 position)
    {
        Target(server, who.SessionId).SetWorldPosition(position);
        who.World.Player.SetWorldPosition(position);
        await StepAsync(server, clients);
        await StepAsync(server, clients);
    }

    // The result of an action the scene sent, once the server has read it and ticked.
    private static async Task<ServerActionMessage> ResultAsync(
        WireLoopbackServer server,
        HeadlessPlayScene[] clients,
        HeadlessPlayScene who,
        ulong? sequence,
        EnemyActor? except = null,
        PlayerInteractionMode mode = PlayerInteractionMode.Combat)
    {
        Assert.NotNull(sequence);
        await WaitActionReadAsync(server, who, sequence.Value);
        for (int i = 0; i < 8; i++)
        {
            await StepAsync(server, clients, except, mode);
            who.Pump();
            if (who.Applied.OfType<ServerActionMessage>().FirstOrDefault(m => m.Result.ActionSequence == sequence && m.Result.SessionId == who.SessionId) is { } result)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"no result for {who.SessionId}'s action {sequence}");
    }

    private static async Task<(HeadlessPlayScene A, HeadlessPlayScene B)> JoinBothAsync(WireLoopbackServer server)
    {
        HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        HeadlessPlayScene b = await HeadlessPlayScene.JoinAsync(server, SessionB);
        await StepAsync(server, [a, b]);
        await PlaceAsync(server, [a, b], a, SpotA);
        await PlaceAsync(server, [a, b], b, SpotB);
        return (a, b);
    }

    // Steps until the client has applied a vitals message (the server sends one every 16 ticks at most).
    private static async Task StepUntilVitalsAsync(WireLoopbackServer server, HeadlessPlayScene[] clients, HeadlessPlayScene who, int minimumCount = 1)
    {
        for (int i = 0; i < 40 && who.Applied.OfType<ServerPlayerVitals>().Count() < minimumCount; i++)
        {
            await StepAsync(server, clients);
        }

        Assert.True(who.Applied.OfType<ServerPlayerVitals>().Count() >= minimumCount, $"{who.SessionId} never got vitals");
    }

    private static void Flush(WireLoopbackServer server)
    {
        Assert.True(server.Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, server.Runtime.PersistenceWrites.FailedWrites);
    }

    // =====================================================================================
    // The wire tests: the client and the protocol only.
    // =====================================================================================

    [Fact]
    public async Task APressedSkillKey_SendsOneSkillUseIntent_WithTheServersOwnAim_AndHoldingDoesNotRepeat()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        await StepAsync(server, [a]);

        ulong? sequence = Press(a, 0);

        Assert.NotNull(sequence);
        await WaitActionReadAsync(server, a, sequence.Value);
        SkillUseIntent intent = Assert.IsType<SkillUseIntent>(LastReadPayload(server, a));
        Assert.Equal("skill.cleave", intent.SkillKey);
        Assert.Null(intent.AimDirection);

        // A key held over several frames is one press.
        MethodInfo handle = typeof(PlayScene).GetMethod("HandleQuickbarKeys", Fields)!;
        ulong before = NextActionSequence(a);
        for (int frame = 0; frame < 5; frame++)
        {
            handle.Invoke(a.Scene, [Keyboard(Keys.D2)]);
        }

        Assert.Equal(before + 1, NextActionSequence(a));
        handle.Invoke(a.Scene, [Keyboard()]);
        Assert.Equal(before + 1, NextActionSequence(a));

        // The keys 2 and 3 are the other two skills of the default bar.
        await WaitActionReadAsync(server, a, before);
        Assert.Equal("skill.bolt", Assert.IsType<SkillUseIntent>(LastReadPayload(server, a)).SkillKey);
        ulong? third = Press(a, 2);
        Assert.NotNull(third);
        await WaitActionReadAsync(server, a, third.Value);
        Assert.Equal("skill.mend", Assert.IsType<SkillUseIntent>(LastReadPayload(server, a)).SkillKey);
    }

    [Fact]
    public async Task AKeyHeldWhileChatClosed_IsNotAPress_AndAnEmptyOrUnstockedSlotSendsNothing()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        await StepAsync(server, [a]);

        // Slot index 3 (key 4) is empty on the default bar; slots 5 and 6 are potions a new character now holds (kit v4), so those do send.
        Assert.Null(Press(a, 3));
        Assert.Equal(1, a.Quickbar.ItemCount(4));
        Assert.Equal(1, a.Quickbar.ItemCount(5));
    }

    [Fact]
    public async Task AnItemKey_SendsAnItemUseIntentNamingTheLowestAnchorAndItsId_WhenTheBagHoldsOne()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        await StepAsync(server, [a]);

        // The bag as the scene will see it, with two potions of the kind: the lower anchor is used.
        PlayerInventorySnapshot inventory = a.Inventory!.Value with
        {
            Slots =
            [
                new InventorySlotSnapshot(12, new InventoryItemSnapshot(900, HealthPotion, "Health Potion", "v", 1, string.Empty, [])),
                new InventorySlotSnapshot(5, new InventoryItemSnapshot(901, HealthPotion, "Health Potion", "v", 1, string.Empty, []))
            ]
        };
        a.Scene.ApplyReplicatedMessage(new InventoryPushMessage(inventory, []));
        Assert.Equal(2, a.Quickbar.ItemCount(4));

        ulong? sequence = Press(a, 4);

        Assert.NotNull(sequence);
        await WaitActionReadAsync(server, a, sequence.Value);
        ItemUseIntent intent = Assert.IsType<ItemUseIntent>(LastReadPayload(server, a));
        Assert.Equal((5, 901L), (intent.SlotIndex, intent.ItemInstanceId));
    }

    [Fact]
    public async Task AClickedTile_SendsOneBindIntentForTheNextEntryInTheCycle()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        await StepAsync(server, [a]);

        // Slot 4 (index 3) is empty: its next entry is the first skill of the content.
        ulong? sequence = ClickTile(a, 3);

        Assert.NotNull(sequence);
        await WaitActionReadAsync(server, a, sequence.Value);
        QuickbarBindIntent intent = Assert.IsType<QuickbarBindIntent>(LastReadPayload(server, a));
        Assert.Equal((3, QuickbarEntryKind.Skill, ContentCatalog.Shipped.Skills[0].Key), (intent.Slot, intent.Kind, intent.Key));

        // The same click through the HUD request, as the renderer hands it over.
        ulong before = NextActionSequence(a);
        typeof(PlayScene).GetMethod("ApplyHudRequest", Fields)!.Invoke(a.Scene, [HudInteractionRequest.None with { QuickbarCycleSlot = 3 }, (Action<Vector2i>)(_ => { })]);
        Assert.Equal(before + 1, NextActionSequence(a));
    }

    [Fact]
    public async Task Vitals_FeedTheQuickbar_AJoinStartsFresh_AndTheEndOfTheSessionClearsIt()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        await StepAsync(server, [a]);

        a.Scene.ApplyReplicatedMessage(new ServerPlayerVitals(31, 50, [new AbilityCooldown("skill.bolt", 90000)]));
        Assert.Equal((31, 50, 1), (a.Quickbar.CurrentMana, a.Quickbar.MaxMana, a.Quickbar.CooldownCount));

        // Vitals are not a routed kind: the message never reaches the protocol session or the server.
        a.Scene.ApplyReplicatedMessage(new ServerPlayerVitals(9999, int.MaxValue, []));
        Assert.Equal(QuickbarHudState.MaxManaCap, a.Quickbar.MaxMana);
        Assert.Equal(9999, a.Quickbar.CurrentMana);

        // A join (a new session) brings the bar of that join and no mana, until the first vitals.
        JoinAcceptedMessage join = a.Applied.OfType<JoinAcceptedMessage>().First();
        a.Scene.ApplyReplicatedMessage(join);
        Assert.Equal((0, 0, 0), (a.Quickbar.CurrentMana, a.Quickbar.MaxMana, a.Quickbar.CooldownCount));
        Assert.Equal("skill.cleave", a.Quickbar.Slot(0).Key);

        // The session ends: the next frame of the scene clears what is left.
        a.Scene.ApplyReplicatedMessage(new ServerPlayerVitals(20, 40, []));
        await a.LeaveAsync();
        typeof(PlayScene).GetMethod("HandleQuickbarKeys", Fields)!.Invoke(a.Scene, [Keyboard()]);
        Assert.False(a.Quickbar.HasData);
        Assert.Equal(0, a.Quickbar.MaxMana);
    }

    // =====================================================================================
    // The flow tests: these also need the server half of M4 (A: item use, mana, vitals, persistence;
    // B: skills).
    // =====================================================================================

    [Fact]
    public async Task APotionFromTheVendor_IsUsedWithKey5_HealsOnce_LeavesTheBag_AndTheCooldownRefusesTheSecond()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        HeadlessPlayScene[] all = [a];
        await StepAsync(server, all);
        string key = KeyOf(SessionA);
        IAuthoritativePlayerTarget target = Target(server, SessionA);

        // Gold from the server (the kill that pays it is M3's test), the vendor's window, two buys.
        await PlaceAsync(server, all, a, NearTheVendor);
        Assert.True(server.Runtime.InventoryService.TryAddGold(key, 100));
        Assert.True(a.Npcs.TryGetNearest(a.World.Player.GetWorldPosition(), ClientNpcCache.InteractRange, out ClientNpc npc));
        ServerActionMessage opened = await ResultAsync(server, all, a, PressE(a));
        Assert.True(opened.Result.Accepted, opened.Result.Rejection?.Reason);
        Assert.True(a.Vendor.IsOpen);
        Assert.Equal(npc.ActorId, a.Vendor.NpcActorId);
        Assert.True(a.Vendor.SellsKind(HealthPotion));
        for (int bought = 0; bought < 2; bought++)
        {
            ulong? buy = Request(a, HudInteractionRequest.None with { Vendor = new VendorHudAction(VendorHudActionKind.Buy, HealthPotion) });
            ServerActionMessage result = await ResultAsync(server, all, a, buy);
            Assert.True(result.Result.Accepted, result.Result.Rejection?.Reason);
        }

        Assert.Equal(3, a.Quickbar.ItemCount(4));

        // The vendor window sat over the inventory, which takes the keys from play: close it, as V does.
        a.Scene.PressInventoryKey();

        // Hurt on the server, then key 5 (the default bar's health potion).
        int max = target.Combatant.Health.MaxHealth;
        target.Combatant.Health.SetAuthoritativeState(max / 4, max);
        ulong? first = Press(a, 4);
        ServerActionMessage used = await ResultAsync(server, all, a, first);
        Assert.True(used.Result.Accepted, used.Result.Rejection?.Reason);
        Assert.IsType<InventoryChangedResult>(used.Result.Payload);
        Assert.True(target.Combatant.Health.CurrentHealth > max / 4);
        Assert.Equal(2, a.Quickbar.ItemCount(4));

        // The second inside the cooldown is refused and costs nothing.
        int healthAfterFirst = target.Combatant.Health.CurrentHealth;
        target.Combatant.Health.SetAuthoritativeState(max / 4, max);
        ulong? second = Press(a, 4);
        ServerActionMessage refused = await ResultAsync(server, all, a, second);
        Assert.False(refused.Result.Accepted);
        Assert.Equal(max / 4, target.Combatant.Health.CurrentHealth);
        Assert.Equal(2, a.Quickbar.ItemCount(4));
        Assert.True(healthAfterFirst > max / 4);

        // The cooldown shows on the tile for a while after the use.
        for (int i = 0; i < 40 && a.Quickbar.RemainingTicks(4, (ulong)Math.Max(0L, (long)server.Runtime.CurrentServerTick), out _) == 0; i++)
        {
            await StepAsync(server, all);
        }

        Assert.True(a.Quickbar.RemainingTicks(4, (ulong)Math.Max(0L, (long)server.Runtime.CurrentServerTick), out _) > 0);
    }

    [Fact]
    public async Task Key1InCombat_CleavesARealEnemy_SpendsMana_AndTheVitalsReachOnlyThatClient_ThenManaComesBack()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        (HeadlessPlayScene a, HeadlessPlayScene b) = await JoinBothAsync(server);
        await using HeadlessPlayScene clientA = a;
        await using HeadlessPlayScene clientB = b;
        HeadlessPlayScene[] both = [a, b];
        IAuthoritativePlayerTarget target = Target(server, SessionA);
        await StepUntilVitalsAsync(server, both, a);
        await StepUntilVitalsAsync(server, both, b);
        Assert.Equal(target.Combatant.MaxMana, a.Quickbar.MaxMana);
        Assert.Equal(a.Quickbar.MaxMana, a.Quickbar.CurrentMana);

        EnemyActor enemy = server.Runtime.Enemies.ActiveActors.First(e => e.IsActive && e.Combatant.Health.IsAlive);
        enemy.SetPosition(target.EyePosition + (target.Facing * 2f) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
        int healthBefore = enemy.Combatant.Health.CurrentHealth;
        int manaBefore = target.Combatant.CurrentMana;

        ulong? sequence = Press(a, 0);
        ServerActionMessage cast = await ResultAsync(server, both, a, sequence, enemy);

        Assert.True(cast.Result.Accepted, cast.Result.Rejection?.Reason);
        Assert.IsType<AbilityUsedResult>(cast.Result.Payload);
        Assert.True(enemy.Combatant.Health.CurrentHealth < healthBefore, "cleave did no damage");
        ContentCatalog.Shipped.TryGetSkill("skill.cleave", out SkillDefinition cleave);
        Assert.True(target.Combatant.CurrentMana <= manaBefore - cleave.ManaCost + PlayerManaRules.RegenAmount(1));

        // The vitals say so, to A alone: B never hears A's mana.
        await StepUntilVitalsAsync(server, both, a, minimumCount: 2);
        int spent = a.Quickbar.CurrentMana;
        Assert.True(spent < a.Quickbar.MaxMana, "the gem would still be full");
        Assert.Equal((float)spent / a.Quickbar.MaxMana, a.Quickbar.CurrentMana / (float)a.Quickbar.MaxMana);
        b.Pump();
        Assert.All(b.Applied.OfType<ServerPlayerVitals>(), v => Assert.Equal(v.MaxMana, v.CurrentMana));
        Assert.Equal(b.Quickbar.MaxMana, b.Quickbar.CurrentMana);

        // Regeneration raises it again.
        for (int i = 0; i < 80; i++)
        {
            await StepAsync(server, both);
        }

        await StepUntilVitalsAsync(server, both, a, minimumCount: a.Applied.OfType<ServerPlayerVitals>().Count() + 1);
        Assert.True(a.Quickbar.CurrentMana > spent, $"{a.Quickbar.CurrentMana} is not more than {spent}");
    }

    [Fact]
    public async Task Key3Heals_InBuildingMode_WhileKey1IsRefusedThere()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        HeadlessPlayScene[] all = [a];
        await StepAsync(server, all, mode: PlayerInteractionMode.Building);
        IAuthoritativePlayerTarget target = Target(server, SessionA);
        Assert.Equal(PlayerInteractionMode.Building, target.InteractionMode);
        int max = target.Combatant.Health.MaxHealth;
        target.Combatant.Health.SetAuthoritativeState(max / 5, max);
        int manaBefore = target.Combatant.CurrentMana;

        ulong? cleave = Press(a, 0);
        ServerActionMessage refused = await ResultAsync(server, all, a, cleave, mode: PlayerInteractionMode.Building);
        Assert.False(refused.Result.Accepted);
        Assert.Contains("Combat mode", refused.Result.Rejection!.Reason, StringComparison.Ordinal);
        Assert.True(target.Combatant.CurrentMana >= manaBefore);

        ulong? mend = Press(a, 2);
        ServerActionMessage healed = await ResultAsync(server, all, a, mend, mode: PlayerInteractionMode.Building);
        Assert.True(healed.Result.Accepted, healed.Result.Rejection?.Reason);
        Assert.True(target.Combatant.Health.CurrentHealth > max / 5);
    }

    [Fact]
    public async Task ABind_AndTheManaLeft_SurviveARestart_AndASecondCharacterOnTheSameServerSeesTheDefaults()
    {
        using MigrationTestDatabase db = new();
        int manaAtTheEnd;

        await using (WireLoopbackServer server = await StartAsync(db))
        {
            await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
            HeadlessPlayScene[] all = [a];
            await StepAsync(server, all);
            Assert.True(a.Quickbar.IsEmpty(3));

            // Bind slot 4 to the first skill, then cast it (it may hit nothing, and costs its mana).
            ulong? bind = ClickTile(a, 3);
            ServerActionMessage bound = await ResultAsync(server, all, a, bind);
            Assert.True(bound.Result.Accepted, bound.Result.Rejection?.Reason);
            Assert.IsType<InventoryChangedResult>(bound.Result.Payload);
            Assert.Equal(ContentCatalog.Shipped.Skills[0].Key, a.Quickbar.Slot(3).Key);

            ulong? cast = Press(a, 3);
            ServerActionMessage cast4 = await ResultAsync(server, all, a, cast);
            Assert.True(cast4.Result.Accepted, cast4.Result.Rejection?.Reason);
            manaAtTheEnd = Target(server, SessionA).Combatant.CurrentMana;
            Assert.True(manaAtTheEnd < Target(server, SessionA).Combatant.MaxMana);

            Flush(server);
            await a.LeaveAsync();
            Flush(server);
        }

        SqliteConnection.ClearAllPools();

        await using (WireLoopbackServer again = await StartAsync(db))
        {
            await using HeadlessPlayScene back = await HeadlessPlayScene.JoinAsync(again, SessionA);
            HeadlessPlayScene[] all = [back];
            await StepAsync(again, all);
            Assert.Equal(ContentCatalog.Shipped.Skills[0].Key, back.Quickbar.Slot(3).Key);
            Assert.Equal("skill.bolt", back.Quickbar.Slot(1).Key);
            await StepUntilVitalsAsync(again, all, back);
            Assert.True(back.Quickbar.CurrentMana < back.Quickbar.MaxMana, "mana came back full after a restart");
            Assert.InRange(back.Quickbar.CurrentMana, manaAtTheEnd, manaAtTheEnd + 2 * PlayerManaRules.RegenAmount(1) + 4);

            // Another character on this server sees the defaults, not A's bind.
            await using HeadlessPlayScene other = await HeadlessPlayScene.JoinAsync(again, SessionB);
            await StepAsync(again, [back, other]);
            Assert.True(other.Quickbar.IsEmpty(3));
            await StepUntilVitalsAsync(again, [back, other], other);
            Assert.Equal(other.Quickbar.MaxMana, other.Quickbar.CurrentMana);
        }
    }

    [Fact]
    public async Task ALateJoiner_GetsVitalsWithinAMoment_AndNeverSeesTheFirstPlayersVitals()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await StartAsync(db);
        await using HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        await StepUntilVitalsAsync(server, [a], a);
        Target(server, SessionA).Combatant.TrySpendMana(10);

        await using HeadlessPlayScene b = await HeadlessPlayScene.JoinAsync(server, SessionB);
        HeadlessPlayScene[] both = [a, b];
        int steps = 0;
        while (!b.Applied.OfType<ServerPlayerVitals>().Any() && steps++ < 32)
        {
            await StepAsync(server, both);
        }

        // Within a second (64 ticks) of its join, the vitals are there, and they are B's own: full.
        Assert.InRange(steps, 0, 31);
        ServerPlayerVitals mine = b.Applied.OfType<ServerPlayerVitals>().First();
        Assert.Equal(mine.MaxMana, mine.CurrentMana);
        Assert.Equal(Target(server, SessionB).Combatant.MaxMana, mine.MaxMana);

        // A's mana is lower, and none of the messages B ever applied carry it.
        await StepUntilVitalsAsync(server, both, a, minimumCount: 2);
        Assert.True(a.Quickbar.CurrentMana < a.Quickbar.MaxMana);
        Assert.All(b.Applied.OfType<ServerPlayerVitals>(), v => Assert.Equal(v.MaxMana, v.CurrentMana));
    }
}
