using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Sessions;
using Dream.Server.Worlds;
using Dream.Server.Hosting;
using Dream.Server.Persistence.Worlds;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server.Persistence.Migrations;

namespace Dream.Tests.Simulation.Runtime;

// Wave I2: a remote player at 0 HP respawns at the world spawn PlayerRespawnRules.DelayTicks server ticks
// after the tick the scan first saw it dead, with full health and every slot unchanged.
public sealed class PlayerRespawnTests
{
    private const int Delay = PlayerRespawnRules.DelayTicks;

    private static readonly PlayerSessionId A = new("resp-a");
    private static readonly PlayerSessionId B = new("resp-b");

    [Fact]
    public void TheDelayIsFiveSecondsOfServerTicks()
    {
        Assert.Equal(320, Delay);
    }

    // The history of one primary-seat death: (tick, alive, x, z, health) for every tick from the one the
    // scan sees the player dead to a few after its respawn.
    private static List<string> RunPrimary(out ulong firstDeadTick)
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        ulong tick = 0;
        WaveIRuntimeKit.Target(runtime, A.Value).SetWorldPosition(new Vector3(20f, 1.5f, 20f));
        WaveIRuntimeKit.Tick(runtime, ref tick, 15);
        WaveIRuntimeKit.Kill(runtime, A.Value);

        firstDeadTick = tick + 1;
        List<string> history = [];
        for (int i = 0; i < Delay + 5; i++)
        {
            WaveIRuntimeKit.Tick(runtime, ref tick);
            IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
            history.Add($"{tick}:{target.IsAlive}:{target.Position.X:F2}:{target.Position.Z:F2}:{target.Combatant.Health.CurrentHealth}");
        }

        return history;
    }

    [Fact]
    public void PrimarySeat_RespawnsAtExactlyTheSeenTickPlusDelay_AtTheSpawnWithFullHealth_InTwoLockstepRuns()
    {
        List<string> first = RunPrimary(out ulong deadTick);
        List<string> second = RunPrimary(out _);
        Assert.Equal(first, second);

        ulong respawnTick = deadTick + Delay;
        foreach (string entry in first)
        {
            string[] fields = entry.Split(':');
            ulong tick = ulong.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
            bool alive = bool.Parse(fields[1]);
            Assert.Equal(tick >= respawnTick, alive);
            if (tick == respawnTick)
            {
                Assert.Equal("100", fields[4]);
                Assert.True(Math.Abs(float.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture) - WaveIRuntimeKit.FarStart.X) < 0.3f, entry);
                Assert.True(Math.Abs(float.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture) - WaveIRuntimeKit.FarStart.Z) < 0.3f, entry);
            }
            else if (tick < respawnTick)
            {
                // Dead where it fell, not at the spawn.
                Assert.Equal("0", fields[4]);
                Assert.True(Math.Abs(float.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture) - 20f) < 1f, entry);
            }
        }
    }

    private static List<string> RunTwoSeats()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        runtime.AttachRemoteSession(B, B.Value);
        ulong tick = 0;
        WaveIRuntimeKit.Target(runtime, A.Value).SetWorldPosition(new Vector3(10f, 1.5f, 10f));
        WaveIRuntimeKit.Target(runtime, B.Value).SetWorldPosition(new Vector3(20f, 1.5f, 20f));
        WaveIRuntimeKit.Tick(runtime, ref tick, 15);

        // The secondary seat dies first, the primary 20 ticks later.
        WaveIRuntimeKit.Kill(runtime, B.Value);
        List<string> history = [];
        for (int i = 0; i < Delay + 40; i++)
        {
            if (i == 20)
            {
                WaveIRuntimeKit.Kill(runtime, A.Value);
            }

            WaveIRuntimeKit.Tick(runtime, ref tick);
            IAuthoritativePlayerTarget a = WaveIRuntimeKit.Target(runtime, A.Value);
            IAuthoritativePlayerTarget b = WaveIRuntimeKit.Target(runtime, B.Value);
            history.Add($"{tick}:b={b.IsAlive}/{b.Combatant.Health.CurrentHealth}/{b.Position.X:F2},{b.Position.Z:F2}:a={a.IsAlive}/{a.Combatant.Health.CurrentHealth}/{a.Position.X:F2},{a.Position.Z:F2}");
        }

        return history;
    }

    [Fact]
    public void SecondaryAndPrimarySeats_EachRespawnOnTheirOwnTimer_InTwoLockstepRuns()
    {
        List<string> first = RunTwoSeats();
        Assert.Equal(first, RunTwoSeats());

        // B died before tick 16 was run (seen at tick 16), A 20 ticks later (seen at tick 36).
        const int bSeen = 16;
        const int aSeen = 36;
        foreach (string entry in first)
        {
            int tick = int.Parse(entry.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(tick >= bSeen + Delay, entry.Contains("b=True", StringComparison.Ordinal));
            if (tick >= aSeen)
            {
                Assert.Equal(tick >= aSeen + Delay, entry.Contains("a=True", StringComparison.Ordinal));
            }
        }

        // B is back at the spawn with full health on its own tick.
        string bBack = first.Single(entry => entry.StartsWith($"{bSeen + Delay}:", StringComparison.Ordinal));
        Assert.Contains("b=True/100/", bBack, StringComparison.Ordinal);
        string[] bXz = bBack.Split("b=True/100/")[1].Split(':')[0].Split(',');
        Assert.True(Math.Abs(float.Parse(bXz[0], System.Globalization.CultureInfo.InvariantCulture) - WaveIRuntimeKit.FarStart.X) < 1.5f, bBack);
        Assert.True(Math.Abs(float.Parse(bXz[1], System.Globalization.CultureInfo.InvariantCulture) - WaveIRuntimeKit.FarStart.Z) < 1.5f, bBack);
    }

    [Fact]
    public void Respawn_KeepsEverySlot()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        runtime.AttachRemoteSession(B, B.Value);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);

        string[] Slots(PlayerSessionId session) =>
        [
            .. runtime.TryCaptureInventorySnapshot(session)!.Value.Slots
                .Select(slot => $"{slot.SlotIndex}:{slot.Item?.ItemInstanceId}:{slot.Item?.ItemKind}:{slot.IsEquipped}")
        ];

        string[] beforeA = Slots(A);
        string[] beforeB = Slots(B);
        Assert.Contains(beforeA, row => row.Split(':')[1].Length > 0);

        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Kill(runtime, B.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick, Delay + 2);

        Assert.True(WaveIRuntimeKit.Target(runtime, A.Value).IsAlive);
        Assert.True(WaveIRuntimeKit.Target(runtime, B.Value).IsAlive);
        Assert.Equal(beforeA, Slots(A));
        Assert.Equal(beforeB, Slots(B));
    }

    [Fact]
    public void ALocalSeatThatDies_IsNeverRespawned()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        runtime.Player.Combatant.Health.ApplyDamage(1_000_000);

        WaveIRuntimeKit.Tick(runtime, ref tick, Delay * 2);

        Assert.False(runtime.Player.Combatant.Health.IsAlive);
        Assert.True(runtime.Sessions.TryGet(runtime.LocalPlayerSessionId, out ConnectedPlayer? local));
        Assert.Equal(0UL, local!.RespawnAtTick);
    }

    [Fact]
    public void Detaching_ClearsTheTimer_AndARejoinWhileStillDeadWaitsAFreshDelay()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        ConnectedPlayer player = runtime.AttachRemoteSession(A, A.Value);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        WaveIRuntimeKit.Kill(runtime, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.Equal(tick + (ulong)Delay, player.RespawnAtTick);

        WaveIRuntimeKit.Tick(runtime, ref tick, 99);
        runtime.DetachRemoteSession(A);
        Assert.Equal(0UL, player.RespawnAtTick);

        // In memory the primary Player keeps its 0 HP, like a character stored dead.
        runtime.AttachRemoteSession(A, A.Value);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        ulong seen = tick;
        Assert.Equal(seen + (ulong)Delay, player.RespawnAtTick);
        WaveIRuntimeKit.Tick(runtime, ref tick, Delay - 1);
        Assert.False(WaveIRuntimeKit.Target(runtime, A.Value).IsAlive);
        WaveIRuntimeKit.Tick(runtime, ref tick);
        Assert.True(WaveIRuntimeKit.Target(runtime, A.Value).IsAlive);
        Assert.Equal(seen + (ulong)Delay, tick);
    }

    // ---- the respawn position ----

    [Fact]
    public void ResolveRespawnPosition_IsTheSpawnWhenItsCellsAreFree()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.Equal(new Vector3(5f, 1.5f, 5f), WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5f, 1.5f, 5f)));
    }

    [Fact]
    public void ResolveRespawnPosition_LiftsPastACubeAtTheSpawnCell_OrOneAboveIt()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(5f, 1f, 5f), withPhysics: false));
        Vector3 lifted = WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5f, 1.5f, 5f));
        Assert.Equal(new Vector3(5f, 2.5f, 5f), lifted);

        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(9f, 2f, 9f), withPhysics: false));
        Vector3 overhead = WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(9f, 1.5f, 9f));
        Assert.Equal(new Vector3(9f, 3.5f, 9f), overhead);
    }

    // A fractured cube leaves no Cube in its cell, only rubble pieces; the cube-only test saw a free cell.
    [Theory]
    [InlineData(5, 1, 5)]
    [InlineData(5, 2, 5)]
    [InlineData(4, 1, 5)]
    public void ResolveRespawnPosition_LiftsPastRubbleThePiecesOfAFracturedCube_WhichLeftNoCubeInItsCell(int cellX, int cellY, int cellZ)
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Vector3 spawn = new(5f, 1.5f, 5f);
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(cellX, cellY, cellZ), withPhysics: false));
        Assert.True(runtime.Map.FractureCubeIntoPieces(new GeometryCellKey(cellX, cellY, cellZ)));
        Assert.Null(runtime.Map.TryGetCubeAtCell(cellX, cellY, cellZ));
        Assert.NotEmpty(runtime.Map.CaptureFracturePieces());

        Vector3 resolved = WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, spawn);

        Assert.Equal(5f, resolved.X);
        Assert.Equal(5f, resolved.Z);
        Assert.True(resolved.Y > spawn.Y, $"respawned at {resolved}, inside the rubble");
        // No piece overlaps the body box (one cell wide, two cells tall from its cell) at the result.
        int bodyCell = (int)MathF.Floor(resolved.Y);
        foreach ((GeometryCellKey _, int _, GeometryFracturePiece piece, GeometryMaterialId _) in runtime.Map.CaptureFracturePieces())
        {
            float half = piece.SideLength * 0.5f;
            bool overlaps = MathF.Abs(piece.Center.X - resolved.X) < 0.5f + half
                && MathF.Abs(piece.Center.Z - resolved.Z) < 0.5f + half
                && piece.Center.Y + half > bodyCell
                && piece.Center.Y - half < bodyCell + 2;
            Assert.False(overlaps, $"piece {piece} overlaps the body at {resolved}");
        }
    }

    [Fact]
    public void ResolveRespawnPosition_IgnoresRubbleFarFromTheSpawn()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false));
        Assert.True(runtime.Map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3)));
        Assert.Equal(new Vector3(5f, 1.5f, 5f), WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5f, 1.5f, 5f)));
    }

    // The 1x1x1 body at X/Z 5.0 overlaps cells 4 and 5 on both axes.
    [Theory]
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    [InlineData(4, 4)]
    public void ResolveRespawnPosition_LiftsPastACubeInANeighbouringCellTheBodyOverlaps(int cellX, int cellZ)
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(cellX, 1f, cellZ), withPhysics: false));
        Assert.Equal(new Vector3(5f, 2.5f, 5f), WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5f, 1.5f, 5f)));
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    [InlineData(4, 4)]
    public void ResolveRespawnPosition_LiftsPastANeighbouringCubeOneAboveTheSpawnCell(int cellX, int cellZ)
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(cellX, 2f, cellZ), withPhysics: false));
        Assert.Equal(new Vector3(5f, 3.5f, 5f), WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5f, 1.5f, 5f)));
    }

    [Fact]
    public void ResolveRespawnPosition_IgnoresACubeInACellTheBodyDoesNotOverlap()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(6f, 1f, 5f), withPhysics: false));
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: false));
        Assert.Equal(new Vector3(5f, 1.5f, 5f), WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5f, 1.5f, 5f)));
    }

    [Fact]
    public void ResolveRespawnPosition_WithAColumnSolidToTheTop_DoesNotThrow_AndStaysInTheMap()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        for (int y = 1; y < runtime.Map.Height; y++)
        {
            runtime.Map.TryAddCube(new Vector3(5f, y, 5f), withPhysics: false);
        }

        Vector3 position = WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5f, 1.5f, 5f));

        Assert.Equal(runtime.Map.Height - 0.5f, position.Y);
        Assert.Equal(5f, position.X);
        Assert.Equal(5f, position.Z);
    }

    [Fact]
    public void ResolveRespawnPosition_ClampsXAndZToTheMap()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Vector3 position = WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(-5f, 1.5f, 999f));
        Assert.Equal(0.5f, position.X);
        Assert.Equal(runtime.Map.Depth - 0.5f, position.Z);
    }

    [Fact]
    public void ABlockedSpawn_RespawnsThePlayerAtTheFirstFreeCell()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(40f, 1f, 40f), withPhysics: false));
        ulong tick = 0;
        WaveIRuntimeKit.Target(runtime, A.Value).SetWorldPosition(new Vector3(20f, 1.5f, 20f));
        WaveIRuntimeKit.Tick(runtime, ref tick, 5);
        WaveIRuntimeKit.Kill(runtime, A.Value);

        WaveIRuntimeKit.Tick(runtime, ref tick, Delay + 1);

        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        Assert.True(target.IsAlive);
        Assert.True(target.Position.Y > 2f, $"respawned at {target.Position}, inside the cube");
    }
}

// Wave I2: the same on a real database, because the rule leans on what a join restores. A character stored
// at 0 HP restores dead at its position and gets a fresh wait; leaving never shortens it; health is per
// world (U2), so a character dead in one world arrives full in another.
public sealed class PlayerRespawnPersistenceTests
{
    private const int Delay = PlayerRespawnRules.DelayTicks;
    private const string Session = "resp-real";

    private static readonly Vector3 DeathPosition = new(30f, 1.5f, 30f);

    private static void Worlds(MigrationTestDatabase db, params string[] args)
    {
        StringWriter stdout = new();
        StringWriter stderr = new();
        int exit = WorldCli.Run(args, () => new SqliteWorldCatalog(db.Schema), stdout, stderr, id => WorldHostLease.TryAcquire(db.DatabasePath, id));
        Assert.True(exit == 0, $"--worlds {string.Join(' ', args)} exited {exit}: {stderr}");
    }

    private static IAuthoritativePlayerTarget Join(WaveIRuntimeKit.RealRun run)
    {
        run.Host.JoinSession(Session, "Alice");
        return WaveIRuntimeKit.Target(run.Runtime, Session);
    }

    // A run that joins, walks to DeathPosition, dies, and leaves; the leave stores 0 HP there.
    private static void StoreDead(MigrationTestDatabase db, string? world = null)
    {
        using WaveIRuntimeKit.RealRun run = new(db, world);
        IAuthoritativePlayerTarget target = Join(run);
        target.SetWorldPosition(DeathPosition);
        run.Advance(5);
        WaveIRuntimeKit.Kill(run.Runtime, Session);
        run.Advance(1);
        Assert.False(target.IsAlive);
        run.Runtime.DetachRemoteSession(new PlayerSessionId(Session));
    }

    private static int HpRow(MigrationTestDatabase db, long worldId = 1)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return Convert.ToInt32(
            db.Scalar($"SELECT current_hp FROM player_world_state WHERE world_id = {worldId} AND player_name = 'Alice';"),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ACharacterStoredDead_RestoresDeadAtItsPosition_AndRespawnsDelayTicksAfterTheJoin_AcrossRestarts()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        StoreDead(db);
        Assert.Equal(0, HpRow(db));

        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            IAuthoritativePlayerTarget target = Join(run);
            Assert.False(target.IsAlive);
            Assert.True(Vector3.Distance(target.Position, DeathPosition) < 0.5f, $"restored at {target.Position}");

            // The first tick sees it dead (tick 1), so it respawns at tick 1 + Delay.
            run.Advance(Delay);
            Assert.False(target.IsAlive);
            Assert.True(Vector3.Distance(target.Position, DeathPosition) < 1f);
            run.Advance(1);
            Assert.True(target.IsAlive);
            Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
            Assert.True(Vector3.Distance(target.Position, new Vector3(5f, 1.5f, 5f)) < 1f, $"respawned at {target.Position}");
        }
    }

    [Fact]
    public void LeavingMidWait_AndRejoining_GivesAFreshWait_AndAKilledProcessGivesTheSame()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        StoreDead(db);

        using (WaveIRuntimeKit.RealRun run = new(db))
        {
            IAuthoritativePlayerTarget target = Join(run);
            run.Advance(101);
            Assert.False(target.IsAlive);

            // Leave at +100, rejoin: the wait restarts from the first tick after the rejoin.
            run.Runtime.DetachRemoteSession(new PlayerSessionId(Session));
            target = Join(run);
            Assert.False(target.IsAlive);
            run.Advance(Delay);
            Assert.False(target.IsAlive);
            run.Advance(1);
            Assert.True(target.IsAlive);
        }

        // The process dies in the middle of a wait: no leave, no final checkpoint. The row still holds 0 HP.
        StoreDead(db);
        using (WaveIRuntimeKit.RealRun crashed = new(db))
        {
            IAuthoritativePlayerTarget target = Join(crashed);
            crashed.Advance(150);
            Assert.False(target.IsAlive);
            crashed.Runtime.DisposeWithoutFinalCheckpoint();
        }

        Assert.Equal(0, HpRow(db));
        using (WaveIRuntimeKit.RealRun after = new(db))
        {
            IAuthoritativePlayerTarget target = Join(after);
            Assert.False(target.IsAlive);
            after.Advance(Delay);
            Assert.False(target.IsAlive);
            after.Advance(1);
            Assert.True(target.IsAlive);
        }
    }

    [Fact]
    public void ACharacterDeadInOneWorld_ArrivesFullInAnother_AndWaitsTheDelayBackInTheFirst()
    {
        using MigrationTestDatabase db = new();
        db.Schema.EnsureSchema();
        StoreDead(db);
        Worlds(db, "create", "second");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using (WaveIRuntimeKit.RealRun other = new(db, "second"))
        {
            IAuthoritativePlayerTarget target = Join(other);
            Assert.True(target.IsAlive);
            Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
            other.Runtime.DetachRemoteSession(new PlayerSessionId(Session));
        }

        using (WaveIRuntimeKit.RealRun first = new(db))
        {
            IAuthoritativePlayerTarget target = Join(first);
            Assert.False(target.IsAlive);
            first.Advance(Delay);
            Assert.False(target.IsAlive);
            first.Advance(1);
            Assert.True(target.IsAlive);
        }
    }
}
