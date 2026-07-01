using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Simulation.Enemies;


public class EnemyPlayerCollisionResolverTests
{
    [Fact]
    public void ResolvePlayerPosition_Preserves_Position_When_Outside_Separation_Distance()
    {
        Vector3 resolved = EnemyPlayerCollisionResolver.ResolvePlayerPosition(
            playerPosition: new Vector3(3f, 1.5f, 0f),
            enemyPosition: Vector3.Zero,
            minimumDistance: 1f);

        Assert.Equal(new Vector3(3f, 1.5f, 0f), resolved);
    }

    [Fact]
    public void ResolvePlayerPosition_Pushes_Player_Out_Of_Enemy_Footprint()
    {
        Vector3 resolved = EnemyPlayerCollisionResolver.ResolvePlayerPosition(
            playerPosition: new Vector3(0.25f, 1.5f, 0f),
            enemyPosition: Vector3.Zero,
            minimumDistance: 1f);

        Assert.Equal(1f, resolved.X, 3);
        Assert.Equal(1.5f, resolved.Y, 3);
        Assert.Equal(0f, resolved.Z, 3);
    }

    [Fact]
    public void ResolvePlayerPosition_Uses_Enemy_Back_Direction_When_Centers_Overlap()
    {
        Vector3 resolved = EnemyPlayerCollisionResolver.ResolvePlayerPosition(
            playerPosition: new Vector3(0f, 1.5f, 0f),
            enemyPosition: Vector3.Zero,
            minimumDistance: 1f,
            fallbackDirection: -Vector3.UnitZ);

        Assert.Equal(0f, resolved.X, 3);
        Assert.Equal(1.5f, resolved.Y, 3);
        Assert.Equal(-1f, resolved.Z, 3);
    }
}
