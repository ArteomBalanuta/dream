using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldMutationResult), "world.mutation")]
[JsonDerivedType(typeof(CombatResolutionResult), "combat.resolution")]
[JsonDerivedType(typeof(PlayerStatsUpdateResult), "player.stats.update")]
public interface IActionResultPayload
{
}
