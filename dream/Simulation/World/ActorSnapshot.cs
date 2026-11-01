using System.Text.Json.Serialization;
using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;

namespace Dream.Simulation.World;

// StarvedTicks is set on a player actor whose session has an accepted command: the ticks the
// server simulated after the tick that consumed that command (LastAcceptedCommandSequences), all
// of them starved ticks that repeated the command and then idled (NETR-3). The position is the
// result of the acknowledged command plus those ticks, so the owning client compares it with its
// prediction StarvedTicks ticks after the ack (ClientPredictionReconciler). Zero in the common
// case, and left off the wire then (also by the reflection contract, which the hand-written
// converter matches byte for byte).
//
// HeldLight (T2f, protocol v6) is the light a player's equipped item casts, so other clients can
// draw a remote player's torch. It is derived from the inventory, so it stays out of
// SimulationStateChecksum. None, the common case, is left off the wire the same way, so a world
// with no light equipped keeps its v5 snapshot bytes.
//
// ModifierDigest (wave H) is a digest of the rolled modifiers of the player's equipped items, for
// SimulationStateChecksum only. It never travels (clients decode 0), is zero when nothing rolled is
// equipped, and zero is not mixed into the hash.
//
// EnemyLevel and VisualKey (wave I, protocol v11) describe an enemy for display. 0 means level 1 and
// a null key means the default visual (the wolf), so a wolf-only world keeps its v10 bytes. Both are
// left off the wire when default. They are authoritative-only: no client field can set them.
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int StarvedTicks = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] HeldLightKind HeldLight = HeldLightKind.None,
    [property: JsonIgnore] ulong ModifierDigest = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int EnemyLevel = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? VisualKey = null);
