using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-PVP-2: one player could seal the world's single respawn point with their own cubes -- a ring around
// the spawn cell and a roof over it. The lift in ResolveRespawnPosition only escaped upwards and stopped as
// soon as the spawn's own cells were clear, which a box around the cell leaves them, so every player who
// died arrived inside the box; the cubes are build-protected from them, so they could never break out, and
// a relog restored the same position. The respawn now looks for a way out and, when there is none, steps to
// the nearest free cell the sealed region does not contain. Build protection itself is untouched -- the
// controls at the end keep that, and keep an ordinary respawn where it was.
public sealed class PvpRespawnSealTests
{
    private static readonly PlayerSessionId A = new("pvp-seal-a");
    private static readonly PlayerSessionId B = new("pvp-seal-b");

    // A wall two cells high around the rectangle [minX..maxX] x [minZ..maxZ], plus a roof over its inside.
    private static void SealBox(Map map, int minX, int maxX, int minZ, int maxZ)
    {
        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                bool border = x == minX || x == maxX || z == minZ || z == maxZ;
                for (int y = 1; y <= 3; y++)
                {
                    if (border ? y <= 2 : y == 3)
                    {
                        Assert.NotNull(map.TryAddCube(new Vector3(x, y, z), withPhysics: false));
                    }
                }
            }
        }
    }

    private static bool BodyCellsAreFree(Map map, Vector3 position)
    {
        int minX = (int)MathF.Floor(position.X - 0.5f + 0.001f);
        int maxX = (int)MathF.Floor(position.X + 0.5f - 0.001f);
        int minZ = (int)MathF.Floor(position.Z - 0.5f + 0.001f);
        int maxZ = (int)MathF.Floor(position.Z + 0.5f - 0.001f);
        int cellY = (int)MathF.Floor(position.Y);
        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                if (map.TryGetCubeAtCell(x, cellY, z)?.IsSolid == true
                    || map.TryGetCubeAtCell(x, cellY + 1, z)?.IsSolid == true)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Fact]
    public void ASpawnSealedInABoxOfCubes_ResolvesToAFreeCellOutsideTheBox()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Vector3 spawn = new(5.5f, 1.5f, 5.5f);
        SealBox(runtime.Map, 4, 6, 4, 6);

        Vector3 resolved = WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, spawn);

        Assert.True(
            Math.Max(MathF.Abs(resolved.X - spawn.X), MathF.Abs(resolved.Z - spawn.Z)) >= 2f,
            $"respawned at {resolved}, still inside the box");
        Assert.True(BodyCellsAreFree(runtime.Map, resolved), $"respawned at {resolved}, inside a cube");
    }

    // The user-visible invariant: a player who dies while another player has boxed the world spawn in must
    // come back somewhere they can act from, not inside the box.
    [Fact]
    public void APlayerWhoDiesWhileTheWorldSpawnIsSealed_RespawnsOutsideTheBox()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, A.Value);
        Vector3 spawn = WaveIRuntimeKit.FarStart;
        SealBox(runtime.Map, 38, 41, 38, 41);
        ulong tick = 0;
        WaveIRuntimeKit.Target(runtime, A.Value).SetWorldPosition(new Vector3(20f, 1.5f, 20f));
        WaveIRuntimeKit.Tick(runtime, ref tick, 2);
        WaveIRuntimeKit.Kill(runtime, A.Value);

        WaveIRuntimeKit.Tick(runtime, ref tick, PlayerRespawnRules.DelayTicks + 2);

        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        Assert.True(target.IsAlive);
        Vector3 at = target.Position;
        Assert.True(
            Math.Max(MathF.Abs(at.X - spawn.X), MathF.Abs(at.Z - spawn.Z)) >= 2f,
            $"respawned at {at}, still inside the box");
        Assert.True(BodyCellsAreFree(runtime.Map, at), $"respawned at {at}, inside a cube");
    }

    // ---- controls ----

    [Fact]
    public void AnOpenSpawn_IsStillTheSpawn()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.Equal(new Vector3(5.5f, 1.5f, 5.5f), WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5.5f, 1.5f, 5.5f)));
    }

    // One cube in the way is not a seal: the body is lifted over it, where it always was.
    [Fact]
    public void ACubeAtTheSpawnCell_StillOnlyLiftsTheBody()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Assert.NotNull(runtime.Map.TryAddCube(new Vector3(5f, 1f, 5f), withPhysics: false));
        Assert.Equal(new Vector3(5.5f, 2.5f, 5.5f), WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, new Vector3(5.5f, 1.5f, 5.5f)));
    }

    // A wall with a way out is not a seal either: nothing is moved.
    [Fact]
    public void AWallWithAGap_LeavesTheRespawnWhereItWas()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        Vector3 spawn = new(5.5f, 1.5f, 5.5f);
        SealBox(runtime.Map, 4, 6, 4, 6);
        Assert.True(runtime.Map.RemoveCube(runtime.Map.TryGetCubeAtCell(6, 1, 5)!));
        Assert.True(runtime.Map.RemoveCube(runtime.Map.TryGetCubeAtCell(6, 2, 5)!));

        Assert.Equal(spawn, WorldSessionRuntime.ResolveRespawnPosition(runtime.Map, spawn));
    }

    // Build protection is unchanged: a cube placed away from the spawn is still placed, still owned, and
    // still safe from another player.
    [Fact]
    public void ACubePlacedAwayFromTheSpawn_IsStillBuilt_AndStillProtectedFromOtherPlayers()
    {
        using WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, "Alice");
        runtime.AttachRemoteSession(B, "Bob");
        // Stated, not inherited: a session now starts in Combat, and a build is refused outside Building.
        // This test is about build PROTECTION, so the building mode is its precondition rather than a
        // default it happens to get.
        runtime.SetSessionInteractionMode(A, PlayerInteractionMode.Building);
        AuthoritativeActionResult placed = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(A, 1, new BuildUseIntent(new Vector3(41.5f, 1.5f, 41.5f))));
        Assert.True(placed.Accepted, placed.Rejection?.Reason);

        Cube? cube = runtime.Map.TryGetCubeAtCell(41, 1, 41);
        Assert.NotNull(cube);
        WorldObjectRegistry registry = (WorldObjectRegistry)typeof(WorldSessionRuntime)
            .GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        WorldObjectRecord? record = registry.TryResolveCubeRecord(runtime.Map, cube!);
        Assert.NotNull(record);
        Assert.False(string.IsNullOrWhiteSpace(record!.OwnerPlayerName));

        MethodInfo canDamage = typeof(WorldSessionRuntime)
            .GetMethod("CanDamageCube", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)canDamage.Invoke(runtime, [A, record])!);
        Assert.False((bool)canDamage.Invoke(runtime, [B, record])!);
    }
}
