using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Simulation.Runtime;

// A swing advancing through its windup and active window costs nothing per tick: the pending list, the
// per-tick cone sweep and the hit set are all reused. The cast itself (the accepted action and its result)
// is allowed to allocate, so a new cast is started every MeleeArcSwing.TotalTicks ticks and
// MeasureBytesPerIteration, which reports the cleanest round, measures rounds that hold no cast.
public sealed class MeleeSwingAllocationTests
{
    private const string Cleave = "skill.cleave";
    private static readonly PlayerSessionId A = new("swing-alloc-a");

    [Fact]
    public void ASteadyStreamOfPendingSwings_AdvancingThroughTheirWindow_AllocatesNothing()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, WaveIRuntimeKit.FarStart, events);
        runtime.AttachRemoteSession(A, "Alice");
        ulong now = 0;
        WaveIRuntimeKit.Tick(runtime, ref now, 10);
        IAuthoritativePlayerTarget target = WaveIRuntimeKit.Target(runtime, A.Value);
        WaveIRuntimeKit.EnterCombat(runtime, A);
        Assert.True(runtime.Sessions.TryGet(A, out ConnectedPlayer? player));

        Vector3 facing = new(target.Facing.X, 0f, target.Facing.Z);
        facing.Normalize();
        for (int i = 0; i < 10; i++)
        {
            Vector3 anchor = target.EyePosition + (facing * 2f);
            Vector3 position = anchor - new Vector3(0f, WolfDefinition.CombatAnchorHeight, 0f);
            EnemyCombatant combatant = new(() => position, events, actorId: 9300 + i, maxHealth: 1_000_000_000, attackDamage: 1, attackIntervalSeconds: 1f);
            runtime.Enemies.Add(new EnemyActor(combatant, position, aggroRange: 0f, attackRange: 0f, moveSpeed: 0f, displayName: "Wolf", level: 1));
        }

        ulong sequence = 0;
        int phase = 0;
        void Step()
        {
            if (phase == 0)
            {
                player!.Cooldowns.Clear();
                target.Combatant.RestoreMana(target.Combatant.MaxMana);
                Assert.True(runtime.ProcessAuthoritativeAction(new PlayerActionMessage(A, ++sequence, new SkillUseIntent(Cleave))).Accepted);
            }

            WaveIRuntimeKit.Tick(runtime, ref now, 1);
            phase = (phase + 1) % MeleeArcSwing.TotalTicks;
        }

        long bytes = AllocationAssert.MeasureBytesPerIteration(Step, iterations: 10, warmupIterations: MeleeArcSwing.TotalTicks * 2);

        Assert.True(sequence > 2, "the swings must actually have been cast");
        Assert.Equal(0, bytes);
    }
}
