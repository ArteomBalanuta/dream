using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Tests.Support;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence;

namespace Dream.Tests.Integration;

// Wave 13 tester: the three classes extracted in wave 13 (PlayerInventoryService,
// AuthoritativeCombatResolver, RuntimePersistenceBridge) exercised together, end to end over
// loopback against the production persistent host (SQLite, deferred joins, join prefetch) and the
// real RemoteClientSession:
// - a join loads (and creates) the persisted profile and inventory (bridge + inventory service);
// - an equip over player.action (inventory service), then attacks that kill an enemy, whose kill
//   XP is persisted (combat resolver -> bridge);
// - a built cube hit until it fractures, its pieces persisted by the single-cell capture (combat
//   resolver -> bridge.PersistFracturePieces), and a second built cube left damaged;
// - leave, profile LRU eviction (MaxDepartedProfiles = 1, so two later visitors push Alpha out
//   together with its inventory), and a rejoin that reads everything back from the database;
// - a host dispose with Alpha still connected (the final checkpoint), then a restart from the
//   database whose restored progression, inventory, health, placed cubes, rubble pieces and
//   enemies must equal the state right before the shutdown.
public sealed class Wave13ExtractedSeamsLoopbackTests(ITestOutputHelper output)
{
    private const string Alpha = "w13-alpha";
    private const int TargetEnemyActorId = 4400;
    private const int TargetEnemyMaxHealth = 30;

    [Fact]
    public async Task JoinEquipKillFractureLeaveEvictRejoin_ThenRestart_RestoresTheSameState()
    {
        using PersistenceTestDatabase database = new();
        WorldState beforeRestart;
        PlayerState alphaBeforeRestart;

        {
            await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
            WorldSessionRuntime runtime = rig.Runtime;
            runtime.MaxDepartedProfiles = 1;
            Driver alpha = new(rig, await rig.JoinAsync(Alpha));
            // The cube and enemy damage below is written for a modest character. A new one is
            // created at the attribute ceiling and one-shots a cube however much health the test
            // gives it, so this states the baseline the damage math assumes. It is saved with the
            // profile on leave, so the rejoin and the restart carry it.
            TestStatBaseline.PinTo(runtime.Player.Combatant);
            List<AuthoritativeActionResult> results = Collect(alpha.Peer);
            // A wolf in aggro range would close in; within 0.9 of a combat ray and 4 m it takes the hit
            // before a nearer cube (AuthoritativeCombatResolver checks enemies first), so the attack phases
            // below would hit it or not depending on timing. The day wolf spawns far out of aggro range and
            // stays at its home for the whole run.
            AssertWolfFarFrom(runtime, alpha.Position());
            await alpha.StepAsync(PlayerInteractionMode.Building, Vector3.UnitZ, attack: false);

            Assert.Equal(1, database.Count($"SELECT COUNT(*) FROM player_profiles WHERE player_name = 'Name {Alpha}';"));
            PlayerState joined = Capture(runtime, Alpha);
            Assert.NotEmpty(ItemIds(joined.Inventory));

            // Equip over player.action (PlayerInventoryService).
            ulong equipSequence = alpha.NextActionSequence();
            alpha.Peer.Session.QueuePlayerAction(new PlayerActionMessage(new PlayerSessionId(Alpha), equipSequence, new InventoryEquipIntent(2)));
            Assert.True(await alpha.Peer.Session.FlushOutboundAsync(TimeSpan.FromSeconds(10)));
            await rig.WaitUntilAsync(
                () => rig.Host.TryGetConnectionContext(alpha.Peer.Connection.ConnectionId, out ServerRoutingContext? context)
                    && context?.LastActionMessage?.ActionSequence >= equipSequence,
                "the server to read Alpha's equip");
            for (int i = 0; i < 64 && !results.Any(result => result.ActionSequence == equipSequence); i++)
            {
                await alpha.StepAsync(PlayerInteractionMode.Building, Vector3.UnitZ, attack: false);
                await rig.DrainAllAsync();
            }

            AuthoritativeActionResult equip = Assert.Single(results, result => result.ActionSequence == equipSequence);
            Assert.True(equip.Accepted, equip.Rejection?.ToString());
            PlayerInventorySnapshot equipped = Assert.IsType<InventoryChangedResult>(equip.Payload).Inventory;
            Assert.NotNull(equipped.EquippedSlot?.Item);

            // Two cubes built beside Alpha: one on its +X ray (fractured below), one on its -X
            // ray (damaged but left standing). Build use is server-originated only (NETR-6).
            Vector3 eye = alpha.Eye();
            Vector3 fractureCell = new(MathF.Floor(eye.X) + 1f, 1f, MathF.Floor(eye.Z));
            Vector3 damagedCell = new(MathF.Floor(eye.X) - 1f, 1f, MathF.Floor(eye.Z));
            Assert.Null(runtime.Map.TryGetCubeAtCell((int)fractureCell.X, 1, (int)fractureCell.Z));
            Assert.Null(runtime.Map.TryGetCubeAtCell((int)damagedCell.X, 1, (int)damagedCell.Z));
            foreach (Vector3 cell in new[] { fractureCell, damagedCell })
            {
                Assert.True(rig.Host.TryEnqueueServerAction(new PlayerActionMessage(new PlayerSessionId(Alpha), alpha.NextActionSequence(), new BuildUseIntent(cell))));
                await alpha.StepAsync(PlayerInteractionMode.Building, Vector3.UnitZ, attack: false);
            }

            Assert.NotNull(runtime.Map.TryGetCubeAtCell((int)fractureCell.X, 1, (int)fractureCell.Z));
            Assert.NotNull(runtime.Map.TryGetCubeAtCell((int)damagedCell.X, 1, (int)damagedCell.Z));
            Assert.Equal(2, database.CountAfterFlush(runtime, "SELECT COUNT(*) FROM placed_cube_instances;"));

            // A weak stationary enemy right in front of Alpha (-Z, away from both cubes); the
            // attacks aimed at its anchor kill it, which awards and persists the kill XP.
            Vector3 alphaPosition = alpha.Position();
            Vector3 enemyPosition = new(alphaPosition.X, alphaPosition.Y, alphaPosition.Z - 1.4f);
            EnemyActor target = new(
                new EnemyCombatant(() => enemyPosition, eventHub: null, actorId: TargetEnemyActorId, maxHealth: TargetEnemyMaxHealth, attackDamage: 1, attackIntervalSeconds: 2f),
                enemyPosition,
                aggroRange: 0.1f,
                attackRange: 0.1f,
                moveSpeed: 0f,
                displayName: "Wave 13 target");
            runtime.Enemies.Add(target);
            int experienceBefore = joined.Progression.Experience;
            for (int i = 0; i < 400 && target.Combatant.Health.IsAlive; i++)
            {
                Vector3 facing = DirectionFrom(alpha.Eye(), target.Combatant.GetCombatAnchorWorldPosition());
                await alpha.StepAsync(PlayerInteractionMode.Combat, facing, attack: true);
            }

            Assert.False(target.Combatant.Health.IsAlive, $"the target enemy survived with {target.Combatant.Health.CurrentHealth} HP");
            await alpha.StepAsync(PlayerInteractionMode.Combat, Vector3.UnitZ, attack: false);
            PlayerState afterKill = Capture(runtime, Alpha);
            Assert.True(
                afterKill.Progression.Experience > experienceBefore || afterKill.Progression.Level > joined.Progression.Level,
                $"no kill XP: {joined.Progression} -> {afterKill.Progression}");
            long persistedExperience = database.CountAfterFlush(runtime, $"SELECT experience FROM player_profiles WHERE player_name = 'Name {Alpha}';");
            long persistedLevel = database.Count($"SELECT level FROM player_profiles WHERE player_name = 'Name {Alpha}';");
            Assert.Equal(afterKill.Progression.Experience, persistedExperience);
            Assert.Equal(afterKill.Progression.Level, persistedLevel);
            Assert.Equal(0, database.Count($"SELECT COUNT(*) FROM enemy_instances WHERE object_id = {TargetEnemyActorId};"));

            // Hit the -X cube a few times (damaged, still standing), then break the +X cube.
            int damagedCubeId = CubeIdAt(runtime, damagedCell);
            int fracturedCubeId = CubeIdAt(runtime, fractureCell);
            // Rows are keyed by ObjectId; the actor id is only the live identity.
            Assert.True(runtime.ObjectIdentities.TryGetObjectId(fracturedCubeId, out ObjectId fracturedObjectId));
            // The damage roll is seeded by the tick, and how many ticks the join and the kill take
            // depends on timing, so one maul hit (a high or critical roll, after the kill's level-up)
            // sometimes broke the 100 HP -X cube outright: it then fractured instead of standing
            // damaged, and its record was gone. Give it enough health that no single hit breaks it.
            int sturdyHealth = 10 * MaxHealthOf(runtime, damagedCubeId)!.Value;
            Assert.True(WorldObjects(runtime).TryGetByActorId(damagedCubeId, out WorldObjectRecord damagedRecord));
            damagedRecord.Health.SetAuthoritativeState(sturdyHealth, sturdyHealth);
            for (int i = 0; i < 200 && HealthOf(runtime, damagedCubeId) is int health && health == MaxHealthOf(runtime, damagedCubeId); i++)
            {
                await alpha.StepAsync(PlayerInteractionMode.Combat, -Vector3.UnitX, attack: true);
            }

            await alpha.StepAsync(PlayerInteractionMode.Combat, -Vector3.UnitX, attack: false);
            Assert.True(HealthOf(runtime, damagedCubeId).HasValue, $"the -X cube {damagedCubeId} at {damagedCell} broke");
            Assert.True(
                HealthOf(runtime, damagedCubeId) < MaxHealthOf(runtime, damagedCubeId),
                $"the -X cube {damagedCubeId} at {damagedCell} took no damage; Alpha at {alpha.Position()}; enemies " +
                string.Join("; ", runtime.Enemies.ActiveActors.Select(enemy => $"{enemy.Combatant.Actor.Id} at {enemy.Position} {enemy.Combatant.Health.CurrentHealth} HP")) +
                "; last results " + string.Join("; ", results.TakeLast(6).Select(result => $"{result.ActionSequence} {result.Accepted} {result.Rejection} {result.Payload}")));
            Assert.NotNull(runtime.Map.TryGetCubeAtCell((int)damagedCell.X, 1, (int)damagedCell.Z));

            GeometryCellKey fractureKey = new((int)fractureCell.X, 1, (int)fractureCell.Z);
            for (int i = 0; i < 800 && runtime.Map.TryGetRubbleAtCell(fractureKey) == null; i++)
            {
                await alpha.StepAsync(PlayerInteractionMode.Combat, Vector3.UnitX, attack: true);
            }

            await alpha.StepAsync(PlayerInteractionMode.Combat, Vector3.UnitZ, attack: false);
            Assert.NotNull(runtime.Map.TryGetRubbleAtCell(fractureKey));
            Assert.False(HealthOf(runtime, fracturedCubeId).HasValue, "the fractured cube's record survived");
            int livePieces = runtime.Map.CaptureFracturePieces().Count(piece => piece.Cell == fractureKey);
            Assert.True(livePieces > 0);
            Assert.Equal(livePieces, database.CountAfterFlush(runtime, $"SELECT COUNT(*) FROM cube_fracture_pieces WHERE origin_object_id = {fracturedObjectId.Value};"));
            Assert.Equal(0, database.Count($"SELECT COUNT(*) FROM placed_cube_instances WHERE object_id = {fracturedObjectId.Value};"));

            // A few more ticks so the rubble settles under physics.
            for (int i = 0; i < 32; i++)
            {
                await alpha.StepAsync(PlayerInteractionMode.Combat, Vector3.UnitZ, attack: false);
            }

            PlayerState beforeLeave = Capture(runtime, Alpha);

            // Leave, then two visitors push Alpha out of the one-entry departed-profile LRU,
            // taking its cached profile and its loaded inventory with it.
            await rig.DisconnectAsync(alpha.Peer);
            foreach (string visitor in new[] { "w13-visitor-1", "w13-visitor-2" })
            {
                Wave12DeltaActorLoopbackTests.Peer peer = await rig.JoinAsync(visitor);
                await rig.StepAsync();
                await rig.DisconnectAsync(peer);
            }

            await rig.StepAsync();
            // (Not TryCaptureInventorySnapshot: it loads the inventory of a departed session it still knows.)
            Assert.False(IsInventoryLoaded(runtime, $"Name {Alpha}"), "Alpha's inventory survived the LRU eviction");
            Assert.True(runtime.DepartedProfileCount <= 1, $"{runtime.DepartedProfileCount} departed profiles cached with an LRU of 1");
            // The visitors spawned beside Alpha's spot; the wolf must still be idle and far away.
            AssertWolfAtHome(runtime);

            // Rejoin: everything comes back from the database.
            alpha = new Driver(rig, await rig.JoinAsync(Alpha));
            await alpha.StepAsync(PlayerInteractionMode.Building, Vector3.UnitZ, attack: false);
            PlayerState rejoined = Capture(runtime, Alpha);
            output.WriteLine($"before leave: {beforeLeave}");
            output.WriteLine($"rejoined:     {rejoined}");
            AssertSamePlayer(beforeLeave, rejoined, comparePosition: true);

            // A last step, then the pre-restart state; the host is disposed with Alpha connected,
            // so the shutdown's final checkpoint has to carry it.
            await alpha.StepAsync(PlayerInteractionMode.Building, Vector3.UnitZ, attack: false);
            alphaBeforeRestart = Capture(runtime, Alpha);
            beforeRestart = CaptureWorld(runtime);
            output.WriteLine($"world before restart: {beforeRestart}");
            Assert.Contains(beforeRestart.Cubes, cube => cube.ActorId == damagedCubeId && cube.CurrentHealth < cube.MaxHealth);
            Assert.DoesNotContain(beforeRestart.Cubes, cube => cube.ActorId == fracturedCubeId);
            Assert.Contains(beforeRestart.Pieces, piece => piece.Cell == fractureKey);

            // Shut the server down the way the dedicated server and the window close do: the
            // service (and its connections) first, then the host, whose runtime writes the final
            // checkpoint. Alpha's client is still connected when this starts.
            await rig.Service.DisposeAsync();
            rig.Host.Dispose();
        }

        SqlitePools.Release(database.DatabasePath);

        {
            await using Wave12DeltaActorLoopbackTests.Rig rig = await StartAsync(database);
            WorldSessionRuntime runtime = rig.Runtime;
            WorldState restored = CaptureWorld(runtime);
            output.WriteLine($"world restored:       {restored}");
            AssertSameWorld(beforeRestart, restored);

            Driver alpha = new(rig, await rig.JoinAsync(Alpha));
            PlayerState rejoined = Capture(runtime, Alpha);
            output.WriteLine($"alpha before restart: {alphaBeforeRestart}");
            output.WriteLine($"alpha after restart:  {rejoined}");
            // The position too: the service's dispose detaches every session before the host's
            // final checkpoint runs, and each detach writes the player's world state.
            AssertSamePlayer(alphaBeforeRestart, rejoined, comparePosition: true);
            await alpha.StepAsync(PlayerInteractionMode.Building, Vector3.UnitZ, attack: false);
        }
    }

    // The day wolf spawns in the far corner, beyond its aggro range from Alpha (and from where the visitors
    // spawn), so nothing parks it: it must simply stay at its home, idle.
    private static void AssertWolfFarFrom(WorldSessionRuntime runtime, Vector3 alphaPosition)
    {
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors, enemy => enemy.EnemyKind == "Wolf");
        Vector3 offset = wolf.Position - alphaPosition;
        offset.Y = 0f;
        Assert.True(offset.Length > wolf.AggroRange + 4f, $"the wolf at {wolf.Position} is {offset.Length} from Alpha at {alphaPosition}");
        AssertWolfAtHome(runtime);
    }

    private static void AssertWolfAtHome(WorldSessionRuntime runtime)
    {
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors, enemy => enemy.EnemyKind == "Wolf");
        Assert.True((wolf.Position - wolf.HomePosition).Length < 1e-3f, $"the wolf left its home: {wolf.Position}");
        Assert.Equal(EnemyBehaviorState.Idle, wolf.BehaviorState);
    }

    private static void AssertSamePlayer(PlayerState expected, PlayerState actual, bool comparePosition)
    {
        Assert.Equal(expected.Progression, actual.Progression);
        Assert.Equal(expected.CurrentHealth, actual.CurrentHealth);
        Assert.Equal(expected.MaxHealth, actual.MaxHealth);
        Assert.Equal(Describe(expected.Inventory), Describe(actual.Inventory));
        if (comparePosition)
        {
            Assert.True((expected.Position - actual.Position).Length < 0.01f, $"position {actual.Position} != {expected.Position}");
        }
    }

    private static void AssertSameWorld(WorldState expected, WorldState actual)
    {
        // Cubes are compared by ObjectId: a restart gives every cube a fresh actor id.
        Assert.Equal(
            expected.Cubes.Select(cube => (cube.ObjectId, cube.Cell, cube.CurrentHealth, cube.MaxHealth, cube.Owner)),
            actual.Cubes.Select(cube => (cube.ObjectId, cube.Cell, cube.CurrentHealth, cube.MaxHealth, cube.Owner)));
        Assert.Equal(expected.Pieces.Count, actual.Pieces.Count);
        for (int i = 0; i < expected.Pieces.Count; i++)
        {
            PieceState e = expected.Pieces[i];
            PieceState a = actual.Pieces[i];
            Assert.True(e.Cell == a.Cell && e.PieceIndex == a.PieceIndex && e.Material == a.Material, $"piece {i}: {a} != {e}");
            Assert.True((e.Center - a.Center).Length < 1e-3f && MathF.Abs(e.Side - a.Side) < 1e-4f, $"piece {i}: {a} != {e}");
        }

        Assert.Equal(expected.Enemies.Count, actual.Enemies.Count);
        for (int i = 0; i < expected.Enemies.Count; i++)
        {
            EnemyState e = expected.Enemies[i];
            EnemyState a = actual.Enemies[i];
            Assert.True(
                e.Id == a.Id && e.Kind == a.Kind && e.Name == a.Name && e.Health == a.Health && e.MaxHealth == a.MaxHealth && e.Level == a.Level,
                $"enemy {i}: {a} != {e}");
            Assert.True((e.Position - a.Position).Length < 1e-3f, $"enemy {e.Id} position {a.Position} != {e.Position}");
        }
    }

    private static Task<Wave12DeltaActorLoopbackTests.Rig> StartAsync(PersistenceTestDatabase database)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(ServerBootstrap.CreatePersistenceBundle(database.DatabasePath, database.SchemaPath));
        return Wave12DeltaActorLoopbackTests.Rig.StartAsync(new NullOutput(), recordExpected: false, host: host);
    }

    private static List<AuthoritativeActionResult> Collect(Wave12DeltaActorLoopbackTests.Peer peer)
    {
        List<AuthoritativeActionResult> results = [];
        peer.Observer = message =>
        {
            if (message is ServerActionMessage action)
            {
                results.Add(action.Result);
            }
        };
        return results;
    }

    private static PlayerState Capture(WorldSessionRuntime runtime, string sessionId)
    {
        ActorSnapshot actor = runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == sessionId);
        PlayerInventorySnapshot inventory = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(sessionId))
            ?? throw new InvalidOperationException($"{sessionId} has no inventory loaded");
        return new PlayerState(actor.Progression!.Value, actor.CurrentHealth, actor.MaxHealth, actor.Position, inventory);
    }

    private static WorldState CaptureWorld(WorldSessionRuntime runtime)
    {
        Map map = runtime.Map;
        CubeState[] cubes = WorldObjects(runtime).GetCubeRecords()
            .Where(record => record.OwnerPlayerName != null && record.SourceObject is Cube)
            .Select(record =>
            {
                Assert.True(map.TryGetCellForCube((Cube)record.SourceObject, out GeometryCellKey cell));
                runtime.ObjectIdentities.TryGetObjectId(record.Actor.Id, out ObjectId objectId);
                return new CubeState(objectId, record.Actor.Id, cell, record.Health.CurrentHealth, record.Health.MaxHealth, record.OwnerPlayerName!);
            })
            .OrderBy(cube => cube.ObjectId)
            .ToArray();
        PieceState[] pieces = map.CaptureFracturePieces()
            .Select(piece => new PieceState(piece.Cell, piece.PieceIndex, piece.Piece.Center, piece.Piece.SideLength, piece.MaterialId))
            .OrderBy(piece => piece.Cell.X).ThenBy(piece => piece.Cell.Y).ThenBy(piece => piece.Cell.Z).ThenBy(piece => piece.PieceIndex)
            .ToArray();
        EnemyState[] enemies = runtime.Enemies.ActiveActors
            .Where(enemy => enemy.Combatant.Health.IsAlive)
            .Select(enemy => new EnemyState(
                runtime.ObjectIdentities.TryGetObjectId(enemy.Combatant.Actor.Id, out ObjectId enemyObject) ? enemyObject.Value : 0,
                enemy.EnemyKind,
                enemy.DisplayName,
                enemy.Position,
                enemy.Combatant.Health.CurrentHealth,
                enemy.Combatant.Health.MaxHealth,
                enemy.Level))
            .OrderBy(enemy => enemy.Id)
            .ToArray();
        return new WorldState(cubes, pieces, enemies);
    }

    private static int CubeIdAt(WorldSessionRuntime runtime, Vector3 cell)
    {
        Cube cube = runtime.Map.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z)!;
        return WorldObjects(runtime).TryResolveCubeRecord(runtime.Map, cube)!.Actor.Id;
    }

    private static int? HealthOf(WorldSessionRuntime runtime, int cubeId)
    {
        return WorldObjects(runtime).TryGetByActorId(cubeId, out WorldObjectRecord record) ? record.Health.CurrentHealth : null;
    }

    private static int? MaxHealthOf(WorldSessionRuntime runtime, int cubeId)
    {
        return WorldObjects(runtime).TryGetByActorId(cubeId, out WorldObjectRecord record) ? record.Health.MaxHealth : null;
    }

    private static WorldObjectRegistry WorldObjects(WorldSessionRuntime runtime)
    {
        return (WorldObjectRegistry)typeof(WorldSessionRuntime)
            .GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime)!;
    }

    private static bool IsInventoryLoaded(WorldSessionRuntime runtime, string playerName)
    {
        return ((Dream.Simulation.Persistence.IRuntimePersistenceHost)runtime).IsPlayerInventoryLoaded(playerName);
    }

    private static Vector3 DirectionFrom(Vector3 origin, Vector3 target)
    {
        Vector3 direction = target - origin;
        return direction.LengthSquared <= 0.0001f ? Vector3.UnitZ : direction.Normalized();
    }

    private static long[] ItemIds(PlayerInventorySnapshot inventory)
    {
        return inventory.AllItems().Select(item => item.ItemInstanceId).Order().ToArray();
    }

    private static string Describe(PlayerInventorySnapshot inventory)
    {
        static string Slot(InventorySlotSnapshot slot) => slot.Item is { } item
            ? $"{slot.SlotIndex}:{item.ItemInstanceId}/{item.ItemKind}/{item.StackCount}"
            : $"{slot.SlotIndex}:-";
        return $"{inventory.PlayerName} {inventory.Columns}x{inventory.Rows} [{string.Join(" ", inventory.Slots.Select(Slot))}] equipped {(inventory.EquippedSlot is { } equipped ? Slot(equipped) : "none")}";
    }

    // Drives one loopback peer: every tick it sends a full command frame (mode, look, attack)
    // and waits until the server has queued it before the tick runs. PlayerCommandHandler sets
    // LastCommandMessage before it enqueues the frame, so a tick run as soon as LastCommandMessage
    // moves could miss the frame (a starved tick that repeats the previous one); the driver also
    // waits for the frame in the command inbox. Only this peer sends commands in this test.
    private sealed class Driver(Wave12DeltaActorLoopbackTests.Rig rig, Wave12DeltaActorLoopbackTests.Peer peer)
    {
        private ulong _commandSequence;
        private ulong _actionSequence;

        public Wave12DeltaActorLoopbackTests.Peer Peer => peer;

        private static BufferedPlayerCommandInbox CommandInbox(Wave12DeltaActorLoopbackTests.Rig rig) =>
            (BufferedPlayerCommandInbox)typeof(DedicatedServerHost)
                .GetField("_commandInbox", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(rig.Host)!;

        public ulong NextActionSequence() => ++_actionSequence;

        public Vector3 Position() => rig.Runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == peer.SessionId).Position;

        public Vector3 Eye() => Position() + new Vector3(0f, 0.35f, 0f);

        public async Task StepAsync(PlayerInteractionMode mode, Vector3 facing, bool attack)
        {
            ulong sequence = ++_commandSequence;
            await peer.Session.SendCommandFrameAsync(new PlayerCommandFrame(
                new PlayerSessionId(peer.SessionId),
                sequence,
                new PlayerModeSwitchIntent(mode),
                PlayerMoveIntent.None,
                new PlayerLookIntent(0f, 0f, facing),
                PrimaryUsePressed: attack));
            await rig.WaitUntilAsync(
                () => rig.Host.TryGetConnectionContext(peer.Connection.ConnectionId, out ServerRoutingContext? context)
                    && context?.LastCommandMessage?.CommandFrame.Sequence >= sequence,
                $"the server to read {peer.SessionId}'s command {sequence}");
            await rig.WaitUntilAsync(() => CommandInbox(rig).HasPending, $"{peer.SessionId}'s command {sequence} to be queued");
            await rig.StepAsync();
            Assert.False(CommandInbox(rig).HasPending, $"a command frame was left in the inbox after the tick for {sequence}");
            await rig.DrainAllAsync();
        }
    }

    private readonly record struct PlayerState(
        PlayerProgressionSnapshot Progression,
        int CurrentHealth,
        int MaxHealth,
        Vector3 Position,
        PlayerInventorySnapshot Inventory)
    {
        public override string ToString() =>
            $"level {Progression.Level} xp {Progression.Experience}/{Progression.ExperienceToNextLevel} hp {CurrentHealth}/{MaxHealth} at {Position}; {Describe(Inventory)}";
    }

    private readonly record struct CubeState(ObjectId ObjectId, int ActorId, GeometryCellKey Cell, int CurrentHealth, int MaxHealth, string Owner);

    private readonly record struct PieceState(GeometryCellKey Cell, int PieceIndex, Vector3 Center, float Side, GeometryMaterialId Material);

    private readonly record struct EnemyState(long Id, string Kind, string Name, Vector3 Position, int Health, int MaxHealth, int Level);

    private sealed record WorldState(IReadOnlyList<CubeState> Cubes, IReadOnlyList<PieceState> Pieces, IReadOnlyList<EnemyState> Enemies)
    {
        public override string ToString() =>
            $"cubes [{string.Join("; ", Cubes)}], {Pieces.Count} pieces in {Pieces.Select(piece => piece.Cell).Distinct().Count()} cells, " +
            $"enemies [{string.Join("; ", Enemies.Select(enemy => string.Create(CultureInfo.InvariantCulture, $"{enemy.Id} {enemy.Kind} {enemy.Health}/{enemy.MaxHealth} at {enemy.Position}")))}]";
    }

    private sealed class NullOutput : ITestOutputHelper
    {
        public void WriteLine(string message)
        {
        }

        public void WriteLine(string format, params object[] args)
        {
        }
    }
}

internal static class Wave13PersistenceTestDatabaseExtensions
{
    // Waits for the runtime's queued persistence writes, then reads one scalar.
    public static long CountAfterFlush(this PersistenceTestDatabase database, WorldSessionRuntime runtime, string sql)
    {
        Assert.True(runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true, "the persistence queue did not flush");
        return database.Count(sql);
    }
}
