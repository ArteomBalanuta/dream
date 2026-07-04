using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Simulation.Enemies;


public class EnemyActorTests
{
    [Fact]
    public void Update_Transitions_To_Chasing_When_Player_Is_Inside_Aggro_Range()
    {
        EnemyActor actor = CreateActor();

        actor.Update(0.25f, new Vector3(3f, 0f, 0f));

        Assert.Equal(EnemyBehaviorState.Chasing, actor.BehaviorState);
    }

    [Fact]
    public void Update_Transitions_To_Attacking_When_Player_Is_In_Melee_Range()
    {
        EnemyActor actor = CreateActor();

        actor.Update(0.25f, new Vector3(0.5f, 0f, 0f));

        Assert.Equal(EnemyBehaviorState.Attacking, actor.BehaviorState);
    }

    [Fact]
    public void Update_Stays_Idle_When_Player_Is_Outside_Aggro_Range()
    {
        EnemyActor actor = CreateActor();

        actor.Update(0.25f, new Vector3(10f, 0f, 0f));

        Assert.Equal(EnemyBehaviorState.Idle, actor.BehaviorState);
    }

    [Fact]
    public void Update_Moves_Toward_Player_While_Chasing()
    {
        EnemyActor actor = CreateActor(moveSpeed: 4f);

        actor.Update(0.5f, new Vector3(4f, 0f, 0f));

        Assert.Equal(new Vector3(2f, 0f, 0f), actor.Position);
        Assert.Equal(Vector3.UnitX, actor.Forward);
    }

    [Fact]
    public void Update_Does_Not_Move_When_Attacking()
    {
        EnemyActor actor = CreateActor(moveSpeed: 4f);

        actor.Update(0.5f, new Vector3(0.75f, 0f, 0f));

        Assert.Equal(Vector3.Zero, actor.Position);
        Assert.Equal(EnemyBehaviorState.Attacking, actor.BehaviorState);
    }

    [Fact]
    public void Update_Stops_At_StandOff_Distance_Before_Reaching_Player()
    {
        EnemyActor actor = CreateActor(moveSpeed: 10f, preferredStandOffDistance: 2f);

        actor.Update(1f, new Vector3(4f, 0f, 0f));

        Assert.Equal(new Vector3(2f, 0f, 0f), actor.Position);
        Assert.Equal(EnemyBehaviorState.Chasing, actor.BehaviorState);
    }

    [Fact]
    public void Update_Uses_Movement_Resolver_When_Chasing()
    {
        EnemyActor actor = CreateActor(moveSpeed: 4f);

        actor.Update(
            0.5f,
            new Vector3(4f, 0f, 0f),
            (_, desiredDelta) => desiredDelta * 0.25f);

        Assert.Equal(new Vector3(0.5f, 0f, 0f), actor.Position);
        Assert.Equal(Vector3.UnitX, actor.Forward);
    }

    [Fact]
    public void Update_Does_Not_Turn_Toward_Player_Outside_Aggro_Range()
    {
        EnemyActor actor = CreateActor();

        actor.Update(0.25f, new Vector3(10f, 0f, 0f));

        Assert.Equal(EnemyBehaviorState.Idle, actor.BehaviorState);
        Assert.Equal(-Vector3.UnitZ, actor.Forward);
    }

    [Fact]
    public void Update_Faces_Resolved_Movement_When_Chase_Path_Is_Adjusted()
    {
        EnemyActor actor = CreateActor(moveSpeed: 4f);

        actor.Update(
            0.5f,
            new Vector3(4f, 0f, 0f),
            (_, _) => Vector3.UnitZ);

        Assert.Equal(Vector3.UnitZ, actor.Position);
        Assert.Equal(Vector3.UnitZ, actor.Forward);
    }

    private static EnemyActor CreateActor(float moveSpeed = 3f, float preferredStandOffDistance = 1.25f)
    {
        EnemyCombatant combatant = new(() => Vector3.Zero, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f);
        return new EnemyActor(
            combatant,
            Vector3.Zero,
            aggroRange: 5f,
            attackRange: 1.25f,
            moveSpeed: moveSpeed,
            preferredStandOffDistance: preferredStandOffDistance);
    }
}
