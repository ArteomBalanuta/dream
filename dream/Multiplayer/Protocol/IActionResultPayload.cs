using System.Text.Json.Serialization;

namespace Dream.Multiplayer.Protocol;


[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldMutationResult), "world.mutation")]
[JsonDerivedType(typeof(CombatResolutionResult), "combat.resolution")]
[JsonDerivedType(typeof(InventoryChangedResult), "inventory.changed")]
[JsonDerivedType(typeof(PlayerStatsUpdateResult), "player.stats.update")]
[JsonDerivedType(typeof(VendorOpenedResult), "vendor.opened")]
public interface IActionResultPayload
{
}
