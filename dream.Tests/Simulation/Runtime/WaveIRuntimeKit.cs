using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Simulation.Runtime;

// Wave I2: what the respawn and loot tests share.
internal static class WaveIRuntimeKit
{
    // Far from the wolves' spawns (the far corner of the 56 m map), so no enemy interferes with a timing test.
    public static readonly Vector3 FarStart = new(40f, 1.5f, 40f);

    public static WorldSessionRuntime CreateRuntime() => new(WorldDefaults.StartupFloorSize, FarStart, new SceneEventHub());

    public static void Tick(WorldSessionRuntime runtime, ref ulong tick, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            runtime.AdvanceAuthoritativeTick(
                ++tick,
                SimulationClock.FixedDeltaSeconds,
                default(PlayerCommandFrame),
                PlayerSimulationTuning.AuthoritativeDefault);
        }
    }

    public static IAuthoritativePlayerTarget Target(WorldSessionRuntime runtime, string session)
    {
        Assert.True(runtime.TryGetSessionPlayerTarget(new PlayerSessionId(session), out IAuthoritativePlayerTarget? target));
        return target!;
    }

    // Ends a seat's join protection, for tests about something other than the first 192 ticks.
    public static void EndProtection(WorldSessionRuntime runtime, PlayerSessionId session)
    {
        Assert.True(runtime.Sessions.TryGet(session, out ConnectedPlayer? player));
        player!.ProtectedThroughTick = 0;
    }

    // The wolf at its home with the player the given offset away from it (offset = wolf minus player). A wolf
    // with no target walks home, so tests that need it to stand still put the player beside the home instead.
    public static void PlaceBeside(EnemyActor wolf, IAuthoritativePlayerTarget target, Vector3 offset)
    {
        wolf.SetPosition(wolf.HomePosition);
        target.SetWorldPosition(wolf.Position - offset);
    }

    public static void Kill(WorldSessionRuntime runtime, string session)
        => Target(runtime, session).Combatant.Health.ApplyDamage(1_000_000);

    // Puts the wolf one metre-ish in front of the session's eye at hit height with the given health and
    // faces the session at it, in Combat mode, so the next combat use hits it. Returns the wolf.
    public static EnemyActor AimWolfAt(WorldSessionRuntime runtime, string session, int wolfHealth)
    {
        IAuthoritativePlayerTarget target = Target(runtime, session);
        EnemyActor wolf = runtime.Enemies.ActiveActors.First();
        Vector3 eye = target.EyePosition;
        Vector3 facing = target.Facing;
        wolf.SetPosition(eye + facing * 2f - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
        wolf.Combatant.Health.SetAuthoritativeState(wolfHealth, wolf.Combatant.Health.MaxHealth);
        return wolf;
    }

    // Puts the session in Combat mode (the primary seat through the runtime, a secondary through a command).
    public static void EnterCombat(WorldSessionRuntime runtime, PlayerSessionId session)
    {
        if (runtime.PrimaryRemoteSessionId == session)
        {
            runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
            return;
        }

        runtime.AdvanceSecondaryRemoteSession(
            new PlayerCommandFrame(
                session,
                ulong.MaxValue / 2,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            SimulationClock.FixedDeltaSeconds,
            PlayerSimulationTuning.AuthoritativeDefault);
    }

    // One combat use by the session on the wolf AimWolfAt put in front of it.
    public static AuthoritativeActionResult HitWolf(WorldSessionRuntime runtime, PlayerSessionId session, ulong actionSequence, int wolfHealth = 1)
    {
        AimWolfAt(runtime, session.Value, wolfHealth);
        IAuthoritativePlayerTarget target = Target(runtime, session.Value);
        return runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(session, actionSequence, new CombatUseIntent(target.EyePosition, target.Facing)));
    }

    // The shipped content with every loot table replaced by one that always drops (drops of them,
    // equal item weights, the four original gear kinds unless kinds says otherwise, the given rarity
    // weights). A one-kind table of 1x1 items makes "room for N more" a count of cells.
    public static ContentCatalog AlwaysDropCatalog(int drops, int common = 1, int uncommon = 1, int rare = 1, string[]? kinds = null)
    {
        string dir = ContentCatalog.ShippedDirectory;
        string items = File.ReadAllText(Path.Combine(dir, ContentCatalog.ItemsFileName));
        string enemies = File.ReadAllText(Path.Combine(dir, ContentCatalog.EnemiesFileName));
        string[] gear = kinds ?? ["gear.leather-cap", "gear.leather-vest", "gear.iron-blade", "gear.ward-lantern"];
        string entries = string.Join(",", gear.Select(kind => "{\"item\":\"" + kind + "\",\"weight\":1}"));
        string table(string key) =>
            "{\"key\":\"" + key + "\",\"drops\":" + drops + ",\"nothingWeight\":0,\"items\":[" + entries + "]," +
            "\"rarity\":{\"common\":" + common + ",\"uncommon\":" + uncommon + ",\"rare\":" + rare + "}}";
        string loot = "{\"version\":1,\"tables\":[" + table("wolf") + "," + table("dire-wolf") + "]}";
        return ContentCatalog.Load(items, enemies, loot);
    }

    // One server run on a real database: the production bootstrap's repositories, write queue and
    // checkpoint writer under a runtime host. Disposing it drains the writes and releases the world.
    public sealed class RealRun : IDisposable
    {
        public RealRun(MigrationTestDatabase db, string? world = null)
        {
            Host = ServerBootstrap.CreateRuntimeHost(
                ServerBootstrap.CreatePersistenceBundle(
                    db.DatabasePath,
                    MigrationTestDatabase.InitScriptPath,
                    start: new ServerStartOptions(world)),
                null);
        }

        public RuntimeAuthoritativeSessionHost Host { get; }

        public WorldSessionRuntime Runtime => Host.Runtime;

        public ulong Now;

        public void Advance(int count) => WaveIRuntimeKit.Tick(Runtime, ref Now, count);

        public void Dispose()
        {
            Assert.True(Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(30)) ?? true);
            Host.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}
