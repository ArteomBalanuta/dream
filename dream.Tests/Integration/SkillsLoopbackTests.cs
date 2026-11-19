using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Simulation.Runtime;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// What the M4 workstream B tests share: a real dedicated server on a real SQLite database with the shipped
// enemies, camps and vendor (EnemyContent = ContentCatalog.Shipped, then SpawnContentNpcs, as the production
// host and M3EndToEndLoopbackTests do), and two real PlayScenes joined over TCP. Actions are sent by the
// harness scene, which numbers them past its commands, so they are never stale after attack commands.
internal sealed class M4BRig : IAsyncDisposable
{
    public const string SessionA = "m4b-a";
    public const string SessionB = "m4b-b";

    private static readonly Vector3 Pen = new(45f, 1f, 45f);
    private readonly Dictionary<string, PlayerInteractionMode> _modes = [];

    private M4BRig(WireLoopbackServer server, HeadlessPlayScene a, HeadlessPlayScene b)
    {
        Server = server;
        A = a;
        B = b;
    }

    public WireLoopbackServer Server { get; }

    public HeadlessPlayScene A { get; }

    public HeadlessPlayScene B { get; }

    public WorldSessionRuntime Runtime => Server.Runtime;

    // The enemy a test keeps next to a player; every other one is sent to the pen on each step.
    public EnemyActor? Held { get; set; }

    public static string KeyOf(string session) => WorldSessionRuntime.ResolveJoinPlayerName(session, $"Name {session}");

    public static async Task<WireLoopbackServer> StartServerAsync(MigrationTestDatabase db, bool pvp = false)
    {
        DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(
            ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath, start: new ServerStartOptions(null)));
        WireLoopbackServer server = await WireLoopbackServer.StartAsync(host);
        server.Runtime.EnemyContent = ContentCatalog.Shipped;
        server.Runtime.SpawnContentNpcs();
        server.Runtime.PvpEnabled = pvp;
        return server;
    }

    // Both players joined and standing apart, away from the vendor.
    public static async Task<M4BRig> StartAsync(MigrationTestDatabase db, bool pvp = false)
    {
        WireLoopbackServer server = await StartServerAsync(db, pvp);
        return await JoinAsync(server);
    }

    public static async Task<M4BRig> JoinAsync(WireLoopbackServer server)
    {
        HeadlessPlayScene a = await HeadlessPlayScene.JoinAsync(server, SessionA);
        HeadlessPlayScene b = await HeadlessPlayScene.JoinAsync(server, SessionB);
        M4BRig rig = new(server, a, b);
        await rig.StepAsync();
        await rig.PlaceAsync(a, new Vector3(20.5f, 1.5f, 20f));
        await rig.PlaceAsync(b, new Vector3(30f, 1.5f, 30f));
        return rig;
    }

    public IAuthoritativePlayerTarget Target(HeadlessPlayScene who) => WaveIRuntimeKit.Target(Runtime, who.SessionId);

    public PlayerCombatant Combatant(HeadlessPlayScene who) => Target(who).Combatant;

    public ConnectedPlayer Player(HeadlessPlayScene who)
    {
        Assert.True(Runtime.Sessions.TryGet(new PlayerSessionId(who.SessionId), out ConnectedPlayer? player));
        return player!;
    }

    public void SetMode(HeadlessPlayScene who, PlayerInteractionMode mode) => _modes[who.SessionId] = mode;

    // Teleports the server's player and the scene's own, then lets the server settle.
    public async Task PlaceAsync(HeadlessPlayScene who, Vector3 position)
    {
        Target(who).SetWorldPosition(position);
        who.World.Player.SetWorldPosition(position);
        await StepAsync();
        await StepAsync();
    }

    // One idle command per scene (carrying its mode), one tick, and every scene has applied that tick's snapshot.
    public async Task StepAsync(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            foreach (EnemyActor enemy in Runtime.Enemies.ActiveActors)
            {
                if (!ReferenceEquals(enemy, Held))
                {
                    enemy.SetPosition(Pen);
                }
            }

            HeadlessPlayScene[] both = [A, B];
            foreach (HeadlessPlayScene client in both)
            {
                PlayerInteractionMode mode = _modes.GetValueOrDefault(client.SessionId, PlayerInteractionMode.Combat);
                await client.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false, mode);
            }

            await Server.TickAsync();
            foreach (HeadlessPlayScene client in both)
            {
                Assert.True(await client.FlushAsync());
            }

            ulong snapshot = Runtime.CurrentServerTick + 1 - (ulong)Server.Service.SnapshotIntervalTicks;
            foreach (HeadlessPlayScene client in both)
            {
                await client.PumpToSnapshotAsync(snapshot);
            }
        }
    }

    // Sends one action as the scene and steps until its result arrived.
    public async Task<ServerActionMessage> ActAsync(HeadlessPlayScene who, IPlayerActionPayload payload)
    {
        ulong sequence = await who.SendActionAsync(payload);
        for (int i = 0; i < 12; i++)
        {
            await StepAsync();
            who.Pump();
            if (who.Applied.OfType<ServerActionMessage>().FirstOrDefault(m => m.Result.ActionSequence == sequence && m.Result.SessionId == who.SessionId) is { } result)
            {
                return result;
            }
        }

        throw new Xunit.Sdk.XunitException($"no result for {who.SessionId}'s action {sequence}");
    }

    private ulong _directSequence = 1_000_000UL;

    // Runs an action on the server itself, as the session, then ticks once. For the uses a client cannot send
    // (build and combat use arrive as primary use of a command) and for tests that read the whole result.
    // Its sequences are far past the harness's own, so a test uses either this or ActAsync, not both.
    public async Task<AuthoritativeActionResult> UseAsync(HeadlessPlayScene who, IPlayerActionPayload payload)
    {
        AuthoritativeActionResult result = Runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(new PlayerSessionId(who.SessionId), ++_directSequence, payload));
        await StepAsync();
        return result;
    }

    // Puts a live enemy the given distance in front of the player's eye, at hit height, and keeps it there.
    public EnemyActor PutEnemyInFront(HeadlessPlayScene who, float distance, int? health = null)
    {
        IAuthoritativePlayerTarget target = Target(who);
        EnemyActor enemy = Runtime.Enemies.ActiveActors.First(e => e.IsActive && e.Combatant.Health.IsAlive);
        enemy.SetPosition(target.EyePosition + (target.Facing * distance) - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f));
        enemy.Combatant.Health.SetAuthoritativeState(health ?? enemy.Combatant.Health.MaxHealth, enemy.Combatant.Health.MaxHealth);
        Held = enemy;
        return enemy;
    }

    public long Gold(HeadlessPlayScene who) => Runtime.TryCaptureInventorySnapshot(new PlayerSessionId(who.SessionId))!.Value.Gold;

    public void Flush()
    {
        Assert.True(Runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, Runtime.PersistenceWrites.FailedWrites);
    }

    public static void AssertRejected(ServerActionMessage result, string reason)
    {
        Assert.False(result.Result.Accepted, "the action was accepted");
        Assert.Contains(reason, result.Result.Rejection!.Reason, StringComparison.Ordinal);
    }

    public static void AssertRejected(AuthoritativeActionResult result, string reason)
    {
        Assert.False(result.Accepted, "the action was accepted");
        Assert.Contains(reason, result.Rejection!.Reason, StringComparison.Ordinal);
    }

    public static AbilityUsedResult AssertUsed(AuthoritativeActionResult result)
    {
        Assert.True(result.Accepted, result.Rejection?.Reason);
        return Assert.IsType<AbilityUsedResult>(result.Payload);
    }

    public static AbilityUsedResult AssertUsed(ServerActionMessage result)
    {
        Assert.True(result.Result.Accepted, result.Result.Rejection?.Reason);
        return Assert.IsType<AbilityUsedResult>(result.Result.Payload);
    }

    public async ValueTask DisposeAsync()
    {
        await A.DisposeAsync();
        await B.DisposeAsync();
        await Server.DisposeAsync();
    }
}

// M4 workstream B, end to end: real skill casts by real scenes against real shipped enemies on a real
// database. The resolver's shapes (arc, wall, caps) are pinned in SkillResolutionTests; these cover what only
// the runtime and the wire can show: the order of the checks, that a rejection spends nothing, the cooldown on
// the session, spawn protection, and the kill paying the caster.
public sealed class SkillsLoopbackTests
{
    [Fact]
    public async Task Cleave_OnARealWolf_SpendsManaAndStartsTheCooldown_AndASecondCastIsRefusedUnspent()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        SkillDefinition cleave = ContentCatalog.Shipped.Skills.Single(s => s.Key == "skill.cleave");
        EnemyActor wolf = rig.PutEnemyInFront(rig.A, 2f);
        PlayerCombatant caster = rig.Combatant(rig.A);
        int maxMana = caster.MaxMana;
        Assert.Equal(maxMana, caster.CurrentMana);
        int wolfBefore = wolf.Combatant.Health.CurrentHealth;

        AbilityUsedResult used = M4BRig.AssertUsed(await rig.ActAsync(rig.A, new SkillUseIntent(cleave.Key)));

        // Accepted at once, but the swing has not landed: no hits are reported and the wolf is untouched.
        Assert.Equal(0, used.TargetsHit);
        Assert.Equal(0, used.DamageApplied);
        Assert.Equal(wolfBefore, wolf.Combatant.Health.CurrentHealth);
        Assert.InRange(caster.CurrentMana, maxMana - cleave.ManaCost, maxMana - cleave.ManaCost + 2);
        Assert.False(rig.Player(rig.A).Cooldowns.IsReady(cleave.Key, rig.Runtime.CurrentServerTick));

        await rig.StepAsync(MeleeArcSwing.WindupTicks + MeleeArcSwing.ActiveTicks);
        Assert.True(wolf.Combatant.Health.CurrentHealth < wolfBefore, "the swing never landed");

        int manaAfter = caster.CurrentMana;
        int wolfAfter = wolf.Combatant.Health.CurrentHealth;
        M4BRig.AssertRejected(await rig.ActAsync(rig.A, new SkillUseIntent(cleave.Key)), "cooldown");
        Assert.True(caster.CurrentMana >= manaAfter, "a refused cast spent mana");
        Assert.Equal(wolfAfter, wolf.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public async Task Cleave_WithFourMana_IsRefusedUnspent_AndWithFiveIsAcceptedAndSpendsFive()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        PlayerCombatant caster = rig.Combatant(rig.A);
        Assert.Equal(5, ContentCatalog.Shipped.Skills.Single(s => s.Key == "skill.cleave").ManaCost);

        caster.SetMana(4);
        M4BRig.AssertRejected(await rig.UseAsync(rig.A, new SkillUseIntent("skill.cleave")), "mana");
        Assert.InRange(caster.CurrentMana, 4, 4 + 2);
        Assert.True(rig.Player(rig.A).Cooldowns.IsReady("skill.cleave", rig.Runtime.CurrentServerTick));

        caster.SetMana(5);
        M4BRig.AssertUsed(await rig.UseAsync(rig.A, new SkillUseIntent("skill.cleave")));
        Assert.InRange(caster.CurrentMana, 0, 2);
        Assert.False(rig.Player(rig.A).Cooldowns.IsReady("skill.cleave", rig.Runtime.CurrentServerTick));
    }

    [Fact]
    public async Task AttackSkills_NeedCombatMode_ButMendWorksInBuildingMode_AndARefusalSpendsNothing()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        rig.SetMode(rig.A, PlayerInteractionMode.Building);
        await rig.StepAsync();
        PlayerCombatant caster = rig.Combatant(rig.A);
        int maxMana = caster.MaxMana;

        M4BRig.AssertRejected(await rig.ActAsync(rig.A, new SkillUseIntent("skill.cleave")), "Combat mode");
        M4BRig.AssertRejected(await rig.ActAsync(rig.A, new SkillUseIntent("skill.bolt")), "Combat mode");
        Assert.Equal(maxMana, caster.CurrentMana);
        Assert.True(rig.Player(rig.A).Cooldowns.IsReady("skill.cleave", rig.Runtime.CurrentServerTick));

        int maxHealth = caster.Health.MaxHealth;
        caster.Health.SetAuthoritativeState(maxHealth - 20, maxHealth);
        AbilityUsedResult mend = M4BRig.AssertUsed(await rig.ActAsync(rig.A, new SkillUseIntent("skill.mend")));
        Assert.Equal(20, mend.HealthRestored);
        Assert.Equal(maxHealth, caster.Health.CurrentHealth);
        Assert.InRange(caster.CurrentMana, maxMana - 15, maxMana - 15 + 2);

        M4BRig.AssertRejected(await rig.ActAsync(rig.A, new SkillUseIntent("skill.nope")), "unknown skill");
    }

    [Fact]
    public async Task BadAim_IsRefusedUnspent_AndAValidAimOverridesTheFacing()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        EnemyActor wolf = rig.PutEnemyInFront(rig.A, 5f);
        PlayerCombatant caster = rig.Combatant(rig.A);
        int maxMana = caster.MaxMana;

        M4BRig.AssertRejected(await rig.ActAsync(rig.A, new SkillUseIntent("skill.bolt", Vector3.Zero)), "aim");
        Assert.Equal(maxMana, caster.CurrentMana);
        Assert.True(rig.Player(rig.A).Cooldowns.IsReady("skill.bolt", rig.Runtime.CurrentServerTick));
        Assert.Equal(wolf.Combatant.Health.MaxHealth, wolf.Combatant.Health.CurrentHealth);

        // Aimed away from the wolf: accepted, mana and cooldown spent, nothing hit.
        AbilityUsedResult away = M4BRig.AssertUsed(await rig.ActAsync(rig.A, new SkillUseIntent("skill.bolt", -Vector3.UnitZ)));
        Assert.Equal(0, away.TargetsHit);
        Assert.True(caster.CurrentMana < maxMana);
        Assert.Equal(wolf.Combatant.Health.MaxHealth, wolf.Combatant.Health.CurrentHealth);

        // Aimed at it (null aim is the facing, +Z here): the same bolt later hits.
        rig.Player(rig.A).Cooldowns.Clear();
        AbilityUsedResult hit = M4BRig.AssertUsed(await rig.ActAsync(rig.A, new SkillUseIntent("skill.bolt")));
        Assert.Equal(1, hit.TargetsHit);
        Assert.True(wolf.Combatant.Health.CurrentHealth < wolf.Combatant.Health.MaxHealth);

        // NaN and infinity cannot be written as JSON, so a hostile one is fed to the runtime directly. Last,
        // because the sequences it uses are far past what the harness numbers its own actions with.
        rig.Player(rig.A).Cooldowns.Clear();
        int manaBefore = caster.CurrentMana;
        ulong sequence = 1_000_000UL;
        foreach (Vector3 bad in new[] { new Vector3(float.NaN, 0f, 1f), new Vector3(0f, float.PositiveInfinity, 0f), new Vector3(0f, 0f, 0.001f) })
        {
            AuthoritativeActionResult rejected = rig.Runtime.ProcessAuthoritativeAction(new PlayerActionMessage(
                new PlayerSessionId(rig.A.SessionId), sequence++, new SkillUseIntent("skill.bolt", bad)));
            Assert.False(rejected.Accepted);
            Assert.Contains("aim", rejected.Rejection!.Reason, StringComparison.Ordinal);
        }

        Assert.Equal(manaBefore, caster.CurrentMana);
        Assert.True(rig.Player(rig.A).Cooldowns.IsReady("skill.bolt", rig.Runtime.CurrentServerTick));
    }

    [Fact]
    public async Task ASkillKill_PaysTheCasterGoldAndExperience_LikeAPrimaryKill()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        EnemyActor wolf = rig.PutEnemyInFront(rig.A, 6f, health: 1);
        PlayerCombatant caster = rig.Combatant(rig.A);
        long goldBefore = rig.Gold(rig.A);
        int experienceBefore = caster.Experience + caster.Level * 100_000;

        AbilityUsedResult used = M4BRig.AssertUsed(await rig.ActAsync(rig.A, new SkillUseIntent("skill.bolt")));

        Assert.Equal(1, used.TargetsHit);
        Assert.False(wolf.Combatant.Health.IsAlive);
        Assert.True(ContentCatalog.Shipped.TryGetEnemy(wolf.EnemyKind, out EnemyTemplate? template));
        Assert.InRange(rig.Gold(rig.A) - goldBefore, Math.Max(1, template!.GoldMin), template.GoldMax);
        Assert.True(caster.Experience + caster.Level * 100_000 > experienceBefore, "no experience for the skill kill");
    }

    [Fact]
    public async Task AttackSkills_EndSpawnProtection_ButMendDoesNot()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        ConnectedPlayer player = rig.Player(rig.A);
        Assert.True(player.ProtectedThroughTick > rig.Runtime.CurrentServerTick, "a joined player starts protected");

        // The mend has to be a real heal for this to test anything: a SelfHeal that would restore
        // nothing is refused outright (BH-SKILL-1), and a refused cast is not what "does not end
        // spawn protection" is about.
        rig.Combatant(rig.A).Health.ApplyDamage(10);
        M4BRig.AssertUsed(await rig.ActAsync(rig.A, new SkillUseIntent("skill.mend")));
        Assert.True(player.ProtectedThroughTick > rig.Runtime.CurrentServerTick);

        M4BRig.AssertUsed(await rig.ActAsync(rig.A, new SkillUseIntent("skill.cleave")));
        Assert.Equal(0UL, player.ProtectedThroughTick);
    }

    [Fact]
    public async Task ADeadPlayer_CannotCast_AndSpendsNothing()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        PlayerCombatant caster = rig.Combatant(rig.A);
        int maxMana = caster.MaxMana;
        caster.Health.ApplyDamage(1_000_000);
        Assert.False(caster.Health.IsAlive);

        M4BRig.AssertRejected(await rig.ActAsync(rig.A, new SkillUseIntent("skill.mend")), "dead");

        Assert.Equal(maxMana, caster.CurrentMana);
        Assert.True(rig.Player(rig.A).Cooldowns.IsReady("skill.mend", rig.Runtime.CurrentServerTick));
    }
}
