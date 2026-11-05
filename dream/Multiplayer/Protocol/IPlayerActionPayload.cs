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
[JsonDerivedType(typeof(InventoryDropIntent), "inventory.drop")]
[JsonDerivedType(typeof(PlayerStatsUpdateIntent), "player.stats.update")]
[JsonDerivedType(typeof(PlayerStatSpendIntent), "player.stats.spend")]
[JsonDerivedType(typeof(GroundPickupIntent), "ground.pickup")]
[JsonDerivedType(typeof(NpcInteractIntent), "npc.interact")]
[JsonDerivedType(typeof(VendorBuyIntent), "vendor.buy")]
[JsonDerivedType(typeof(VendorSellIntent), "vendor.sell")]
[JsonDerivedType(typeof(SkillUseIntent), "skill.use")]
[JsonDerivedType(typeof(ItemUseIntent), "item.use")]
[JsonDerivedType(typeof(QuickbarBindIntent), "quickbar.bind")]
[JsonDerivedType(typeof(PartyInviteIntent), "party.invite")]
[JsonDerivedType(typeof(PartyRespondIntent), "party.respond")]
[JsonDerivedType(typeof(PartyLeaveIntent), "party.leave")]
[JsonDerivedType(typeof(TradeRequestIntent), "trade.request")]
[JsonDerivedType(typeof(TradeRespondIntent), "trade.respond")]
[JsonDerivedType(typeof(TradeOfferIntent), "trade.offer")]
[JsonDerivedType(typeof(TradeLockIntent), "trade.lock")]
[JsonDerivedType(typeof(TradeConfirmIntent), "trade.confirm")]
[JsonDerivedType(typeof(TradeCancelIntent), "trade.cancel")]
[JsonDerivedType(typeof(QuestAcceptIntent), "quest.accept")]
[JsonDerivedType(typeof(QuestTurnInIntent), "quest.turn-in")]
[JsonDerivedType(typeof(QuestAbandonIntent), "quest.abandon")]
public interface IPlayerActionPayload
{
}
