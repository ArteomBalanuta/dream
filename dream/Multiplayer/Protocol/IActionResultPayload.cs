using System.Text.Json.Serialization;

namespace Dream.Multiplayer.Protocol;


[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldMutationResult), "world.mutation")]
[JsonDerivedType(typeof(CombatResolutionResult), "combat.resolution")]
[JsonDerivedType(typeof(InventoryChangedResult), "inventory.changed")]
[JsonDerivedType(typeof(PlayerStatsUpdateResult), "player.stats.update")]
public interface IActionResultPayload
{
}
