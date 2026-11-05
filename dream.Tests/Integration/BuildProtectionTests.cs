using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// M4 build protection, end to end on a real database: a placed cube can only be damaged by its owner.
// A stranger's attack is refused before any damage or write; the owner's goes through; a cube with no owner
// (terrain, or one whose builder was deleted) stays open to everyone, which the existing terrain and
// BuildingsOutliveBuilder suites keep proving; the owner is stored, so protection survives a restart.
[Collection(TimingSensitiveCollection.Name)]
public sealed class BuildProtectionTests
{
    private const string StrangerRefusal = "That belongs to someone else.";

    // The tests expect an owned cube to survive one hit and break on a later one. A profile-backed new
    // character is created at Strength 10, so player.min_damage (5 + strength * 2) is 25 against a 100 hp
    // cube and it takes several hits. The seed to the baseline's Strength 10 (25-50 per hit) is now what
    // production already gives; it is kept so the tests state the stats they were written for.
    // The rig builds its own bundle, so the tables are created here first (EnsureSchema is idempotent).
    private static Task<M4BRig> StartRigAsync(MigrationTestDatabase db)
    {
        db.Schema.EnsureSchema();
        TestStatBaseline.SeedMissingProfiles(db.Schema, M4BRig.KeyOf(M4BRig.SessionA), M4BRig.KeyOf(M4BRig.SessionB));
        return M4BRig.StartAsync(db);
    }

    private static WorldObjectRegistry Registry(WorldSessionRuntime runtime) =>
        (WorldObjectRegistry)typeof(WorldSessionRuntime).GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;

    private static WorldObjectRecord RecordAt(M4BRig rig, (int X, int Y, int Z) cell)
    {
        Cube? cube = rig.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z);
        Assert.NotNull(cube);
        WorldObjectRecord? record = Registry(rig.Runtime).TryResolveCubeRecord(rig.Runtime.Map, cube!);
        Assert.NotNull(record);
        return record!;
    }

    // A, in Building mode, places a cube 2.5 m in front of itself at eye height, then goes back to Combat.
    private static async Task<(int X, int Y, int Z)> PlaceCubeAsync(M4BRig rig)
    {
        rig.SetMode(rig.A, PlayerInteractionMode.Building);
        await rig.StepAsync();
        int y = (int)MathF.Floor(rig.Target(rig.A).EyePosition.Y);
        AuthoritativeActionResult placed = await rig.UseAsync(rig.A, new BuildUseIntent(new Vector3(20.5f, y + 0.5f, 22.5f)));
        Assert.True(placed.Accepted, placed.Rejection?.Reason);
        rig.SetMode(rig.A, PlayerInteractionMode.Combat);
        await rig.StepAsync();
        return (20, y, 22);
    }

    // B stands in the same column, 3.5 m short of the cube, looking at it.
    private static Task StandBeforeTheCubeAsync(M4BRig rig) => rig.PlaceAsync(rig.B, new Vector3(20.5f, 1.5f, 18.5f));

    private static long StoredHealth(M4BRig rig, MigrationTestDatabase db)
    {
        rig.Flush();
        return Convert.ToInt64(db.Scalar("SELECT current_health FROM placed_cube_instances;"));
    }

    private static Task<AuthoritativeActionResult> HitAsync(M4BRig rig, HeadlessPlayScene who) =>
        rig.UseAsync(who, new CombatUseIntent(Vector3.Zero, Vector3.UnitZ));

    [Fact]
    public async Task AStranger_CannotDamageAPlacedCube_TheOwnerCan_AndTheOwnerCanBreakIt()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await StartRigAsync(db);
        (int X, int Y, int Z) cell = await PlaceCubeAsync(rig);
        WorldObjectRecord record = RecordAt(rig, cell);
        Assert.Equal(M4BRig.KeyOf(M4BRig.SessionA), record.OwnerPlayerName);
        int full = record.Health.MaxHealth;
        long storedFull = StoredHealth(rig, db);
        Assert.Equal(full, storedFull);
        await StandBeforeTheCubeAsync(rig);

        // The stranger: refused, and neither the record nor the row moved.
        AuthoritativeActionResult refused = await HitAsync(rig, rig.B);
        M4BRig.AssertRejected(refused, StrangerRefusal);
        Assert.Equal(full, record.Health.CurrentHealth);
        Assert.Equal(storedFull, StoredHealth(rig, db));
        Assert.NotNull(rig.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));

        // The owner: damage lands and is written.
        AuthoritativeActionResult owned = await HitAsync(rig, rig.A);
        Assert.True(owned.Accepted, owned.Rejection?.Reason);
        CombatResolutionResult hit = Assert.IsType<CombatResolutionResult>(owned.Payload);
        Assert.True(hit.DamageApplied > 0);
        Assert.Equal(full - hit.DamageApplied, record.Health.CurrentHealth);

        // ... and finish it once the attack is ready again.
        record.Health.SetAuthoritativeState(1, full);
        await rig.StepAsync(100);
        AuthoritativeActionResult broke = await HitAsync(rig, rig.A);
        Assert.True(Assert.IsType<CombatResolutionResult>(broke.Payload).TargetDied);
        Assert.Null(rig.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
    }

    [Fact]
    public async Task AStranger_IsRefused_EvenWhenTheRegistryLostTheOwner_BecauseTheRecordIsRepairedBeforeTheGate()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await StartRigAsync(db);
        (int X, int Y, int Z) cell = await PlaceCubeAsync(rig);
        WorldObjectRecord record = RecordAt(rig, cell);
        int full = record.Health.MaxHealth;
        rig.Flush();

        // The registry link is lost: a fresh record with a new identity and no owner replaces the real one.
        Cube cube = rig.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z)!;
        WorldObjectRecord stripped = Registry(rig.Runtime).ReplaceCubeRecordIdentity(
            cube, rig.Runtime.Map, record.Actor.Id + 1000, full, full, ownerPlayerName: null);
        Assert.Null(stripped.OwnerPlayerName);
        await StandBeforeTheCubeAsync(rig);

        M4BRig.AssertRejected(await HitAsync(rig, rig.B), StrangerRefusal);

        Assert.Equal(full, RecordAt(rig, cell).Health.CurrentHealth);
        Assert.Equal(full, StoredHealth(rig, db));
    }

    [Fact]
    public async Task ACubeWithNoOwner_CanBeDamagedByAnyone()
    {
        using MigrationTestDatabase db = new();
        await using M4BRig rig = await StartRigAsync(db);
        int y = (int)MathF.Floor(rig.Target(rig.A).EyePosition.Y);
        Cube? cube = rig.Runtime.Map.TryAddCube(new Vector3(20.5f, y + 0.5f, 22.5f), isStatic: true, withPhysics: false);
        Assert.NotNull(cube);
        WorldObjectRecord record = Registry(rig.Runtime).RegisterCube(cube!, rig.Runtime.Map, ownerPlayerName: null);
        await StandBeforeTheCubeAsync(rig);

        AuthoritativeActionResult result = await HitAsync(rig, rig.B);

        Assert.True(result.Accepted, result.Rejection?.Reason);
        Assert.True(Assert.IsType<CombatResolutionResult>(result.Payload).DamageApplied > 0);
        Assert.True(record.Health.CurrentHealth < record.Health.MaxHealth);
    }

    [Fact]
    public async Task Protection_SurvivesARestart_BecauseTheOwnerIsStored()
    {
        using MigrationTestDatabase db = new();
        (int X, int Y, int Z) cell;
        int full;
        await using (M4BRig first = await StartRigAsync(db))
        {
            cell = await PlaceCubeAsync(first);
            full = RecordAt(first, cell).Health.MaxHealth;
            first.Flush();
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(M4BRig.KeyOf(M4BRig.SessionA), db.Scalar("SELECT owner_player_name FROM placed_cube_instances;"));

        await using M4BRig rig = await M4BRig.JoinAsync(await M4BRig.StartServerAsync(db));
        WorldObjectRecord record = RecordAt(rig, cell);
        Assert.Equal(M4BRig.KeyOf(M4BRig.SessionA), record.OwnerPlayerName);
        await StandBeforeTheCubeAsync(rig);

        M4BRig.AssertRejected(await HitAsync(rig, rig.B), StrangerRefusal);
        Assert.Equal(full, record.Health.CurrentHealth);
        Assert.Equal(full, StoredHealth(rig, db));

        AuthoritativeActionResult owned = await HitAsync(rig, rig.A);
        Assert.True(owned.Accepted, owned.Rejection?.Reason);
        Assert.True(record.Health.CurrentHealth < full);
    }

    // "A brand-new character is not a god", pinned end to end. This deliberately does NOT go through
    // StartRigAsync: that seeds TestStatBaseline profiles, and seeding is what hid the 1000 origin
    // (a39702e6) undetected: every assertion that touched it derived its expectation from the constant. Here SessionA is created by the real profile path on join, so whatever
    // PersistedPlayerProfile.CreateDefault hands out is what is measured.
    [Fact]
    public async Task ABrandNewProfileBackedCharacter_IsNotAGod_AndOneSwingDoesNotDestroyAPlacedCube()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        await using M4BRig rig = await M4BRig.StartAsync(db);
        PlayerCombatant player = rig.Combatant(rig.A);

        // Created at the origin, nothing spent, nothing seeded.
        Assert.Equal(1, player.Level);
        Assert.Equal(PlayerStatRules.NewCharacterAttribute, player.BaseStats.Strength);
        Assert.Equal(PlayerStatRules.NewCharacterAttribute, player.BaseStats.Agility);
        Assert.Equal(PlayerStatRules.NewCharacterAttribute, player.BaseStats.Vitality);

        // The profile's own numbers: derived from BASE stats, before the worn starter kit (+2 Strength,
        // +0.25 attack speed). player.Defense and player.Health.MaxHealth are effective-stat reads
        // (Defense from post-gear Agility, MaxHealth from DerivedStats); they equal the naked values here
        // only because the kit adds no Agility or Vitality. Symbolic first: they follow the formulas from
        // the constant.
        int n = PlayerStatRules.NewCharacterAttribute;
        PlayerCombatDerivedStats naked = player.BaseStats.Derive(level: player.Level);
        Assert.Equal(50 + (n * 5), naked.MaxHealth);
        Assert.Equal(5 + (n * 2), naked.MinDamage);
        Assert.Equal(10 + (n * 4), naked.MaxDamage);
        Assert.Equal(PlayerStatRules.Defense(n), player.Defense);

        // Literals: the DESIGNED values. They are written out on purpose, because a test that only follows
        // the constant passes at any origin. At the 1000 origin these were 5050 hp, 2005-4010 damage and
        // Defense 250, and a new character one-shot everything and could not be killed.
        Assert.Equal(100, naked.MaxHealth);
        Assert.Equal(100, player.Health.MaxHealth);
        Assert.Equal(25, naked.MinDamage);
        Assert.Equal(50, naked.MaxDamage);
        Assert.Equal(2, player.Defense);

        // What the character really swings with includes the starter kit: at least the naked numbers, and
        // still far below a 100 hp cube's worth of one-shot (the kit's single-digit bonus is meaningful).
        Assert.InRange(player.DerivedStats.MinDamage, 25, 49);
        Assert.InRange(player.DerivedStats.MaxDamage, 50, 99);
        // Exact: the hammer's +2 Strength reaches the sheet (the Strength term of the geared numbers).
        Assert.Equal(5 + (2 * (n + 2)), player.DerivedStats.MinDamage);
        Assert.Equal(10 + (4 * (n + 2)), player.DerivedStats.MaxDamage);
        Assert.Equal(29, player.DerivedStats.MinDamage);
        Assert.Equal(58, player.DerivedStats.MaxDamage);
        Assert.Equal(100, player.DerivedStats.MaxHealth);

        // Attack speed is nowhere near the cap (at the 1000 origin it sat pinned on it, so Agility and gear
        // attack speed were inert).
        Assert.True(player.Stats.AttackSpeed < PlayerCombatStats.MaxAttackSpeed,
            $"attack speed {player.Stats.AttackSpeed} is at the cap {PlayerCombatStats.MaxAttackSpeed}");
        Assert.True(player.Stats.AttackSpeed < PlayerCombatStats.MaxAttackSpeed - 1f,
            $"attack speed {player.Stats.AttackSpeed} is within 1.0 of the cap");

        // Behaviour: one real swing at a 100 hp player-placed cube leaves it standing.
        (int X, int Y, int Z) cell = await PlaceCubeAsync(rig);
        WorldObjectRecord record = RecordAt(rig, cell);
        Assert.Equal(100, record.Health.MaxHealth);
        AuthoritativeActionResult swing = await HitAsync(rig, rig.A);
        Assert.True(swing.Accepted, swing.Rejection?.Reason);
        CombatResolutionResult hit = Assert.IsType<CombatResolutionResult>(swing.Payload);
        Assert.True(hit.DamageApplied > 0);
        Assert.True(hit.DamageApplied < 100, $"one swing did {hit.DamageApplied} to a 100 hp cube");
        Assert.False(hit.TargetDied);
        Assert.NotNull(rig.Runtime.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
        Assert.Equal(100 - hit.DamageApplied, record.Health.CurrentHealth);
        Assert.True(record.Health.CurrentHealth > 0);
    }
}
