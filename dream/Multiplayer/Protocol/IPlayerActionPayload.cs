using System.Text.Json.Serialization;

namespace Dream.Multiplayer.Protocol;


[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(BuildUseIntent), "build.use")]
[JsonDerivedType(typeof(CombatUseIntent), "combat.use")]
[JsonDerivedType(typeof(InventoryEquipIntent), "inventory.equip")]
[JsonDerivedType(typeof(PlayerStatsUpdateIntent), "player.stats.update")]
public interface IPlayerActionPayload
{
}
