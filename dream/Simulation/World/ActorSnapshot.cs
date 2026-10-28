using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;

namespace Dream.Simulation.World;

// StarvedTicks is set on a player actor whose session has an accepted command: the ticks the
// server simulated after the tick that consumed that command (LastAcceptedCommandSequences), all
// of them starved ticks that repeated the command and then idled (NETR-3). The position is the
// result of the acknowledged command plus those ticks, so the owning client compares it with its
// prediction StarvedTicks ticks after the ack (ClientPredictionReconciler). Zero in the common
// case, and left off the wire then (also by the reflection contract, which the hand-written
// converter matches byte for byte).
public readonly record struct ActorSnapshot(
    int ActorId,
    CreatureKind Kind,
    string? SessionId,
    Vector3 Position,
    Vector3 Facing,
    int CurrentHealth,
    int MaxHealth,
    bool IsAlive,
    PlayerInteractionMode? InteractionMode = null,
    PlayerCombatStatsSnapshot? CombatStats = null,
    string? DisplayName = null,
    PlayerProgressionSnapshot? Progression = null,
    EnemyBehaviorState? EnemyBehaviorState = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int StarvedTicks = 0);
