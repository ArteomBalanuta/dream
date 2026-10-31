using System.Text.Json.Serialization;

namespace Dream.Multiplayer.Protocol;


[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(BuildUseIntent), "build.use")]
[JsonDerivedType(typeof(BuildMaterialSelectIntent), "build.material")]
[JsonDerivedType(typeof(CombatUseIntent), "combat.use")]
[JsonDerivedType(typeof(InventoryEquipIntent), "inventory.equip")]
[JsonDerivedType(typeof(InventoryUnequipIntent), "inventory.unequip")]
[JsonDerivedType(typeof(InventoryMoveIntent), "inventory.move")]
[JsonDerivedType(typeof(InventoryDestroyIntent), "inventory.destroy")]
[JsonDerivedType(typeof(PlayerStatsUpdateIntent), "player.stats.update")]
public interface IPlayerActionPayload
{
}
