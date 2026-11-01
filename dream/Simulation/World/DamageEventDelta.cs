using System.Text.Json.Serialization;
using Dream.Simulation.Combat;

namespace Dream.Simulation.World;

// HitKind is the trailing wire field "hit" (protocol v12): written only for a critical or incoming
// hit, and read back as Regular when absent. The client treats an undefined value as Regular.
public readonly record struct DamageEventDelta(
    int ActorId,
    CreatureKind Kind,
    int Amount,
    bool ActorDied,
    int CurrentHealth,
    int MaxHealth,
    [property: JsonPropertyName("hit")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] FloatingCombatTextKind HitKind = FloatingCombatTextKind.Regular)
{
    // ToString is part of what tests hash (the tick goldens print damage events, and their script
    // does land critical hits), so it prints exactly the members it printed before HitKind existed.
    // Equality still compares HitKind.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("ActorId = ").Append(ActorId.ToString())
            .Append(", Kind = ").Append(Kind.ToString())
            .Append(", Amount = ").Append(Amount.ToString())
            .Append(", ActorDied = ").Append(ActorDied.ToString())
            .Append(", CurrentHealth = ").Append(CurrentHealth.ToString())
            .Append(", MaxHealth = ").Append(MaxHealth.ToString());
        return true;
    }
}
