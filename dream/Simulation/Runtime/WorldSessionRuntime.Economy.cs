using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;

namespace Dream.Simulation.Runtime;

// Gold and vendors (M3 workstream B). Gold is a per-character wallet kept by PlayerInventoryService; a
// kill credits it directly, with a notice, and a vendor trades it for items. The server keeps no vendor
// session: every buy and sell checks the NPC id and the range again, and every write is a wallet delta
// inside the action's own closure (see PlayerInventoryService.Vendor.cs).
public sealed partial class WorldSessionRuntime
{
    private const ulong GoldSalt = 0x676F6C642D726F6CUL; // "gold-rol"

    // Credits the killer's wallet with the kill's gold; called by GrantLoot once the killer's inventory
    // is known to be loaded. seedHash is the hash of the killer's session id. The amount is drawn from the
    // enemy's goldMin..goldMax with a fresh generator seeded from counters only (like the loot roll), so it
    // does not depend on the order of anything else. Nothing for an enemy kind that pays none, and
    // nothing when the wallet is too full to take it.
    private void GrantKillGold(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick, ulong seedHash)
    {
        // The template of the enemy that died comes from the content the enemies were spawned from.
        if (!EnemyContent.TryGetEnemy(enemyKind, out EnemyTemplate? template)
            || template.GoldMax <= 0
            || !_sessions.TryGet(killer, out ConnectedPlayer? player)
            || player == null)
        {
            return;
        }

        DeterministicRandom random = new(Seed.Combine(
            Seed.Combine(GoldSalt, SimulationSeed, tick, seedHash),
            Seed.FromInt(enemyActorId)));
        long amount = random.NextInt(template.GoldMin, template.GoldMax + 1);
        if (amount <= 0)
        {
            return;
        }

        if (!_inventory.TryAddGold(player.CharacterKey.Value, amount))
        {
            NetworkDiagnosticsLog.Write("server.loot.skipped", $"reason=gold-not-credited kind={enemyKind}");
            return;
        }

        // The notice names the gold ("12 gold"); the inventory snapshot of the push carries the wallet.
        AddLootNotice(
            killer,
            new LootDrop($"{amount} gold", ItemRarity.Common),
            lost: false,
            inventoryChanged: true);
    }

    // Resolves the NPC an action names for the acting player: the outcome says why not (TooFar for an
    // unknown id or a player out of range). Never spends the budget.
    private InventoryEquipOutcome TryResolveVendor(ConnectedPlayer player, int npcActorId, out SpawnedNpc npc)
    {
        npc = null!;
        if (!TryFindNpc(npcActorId, out SpawnedNpc? found)
            || !TryResolveAuthoritativePlayerTarget(player.SessionId, out _, out IAuthoritativePlayerTarget? target)
            || target == null
            || !VendorRules.InRange(target.Position, found.Position))
        {
            return InventoryEquipOutcome.TooFar;
        }

        npc = found;
        return InventoryEquipOutcome.Equipped;
    }

    private AuthoritativeActionResult ProcessNpcInteractAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        NpcInteractIntent intent)
    {
        InventoryEquipOutcome outcome = TryResolveVendor(player, intent.NpcActorId, out SpawnedNpc npc);
        if (outcome != InventoryEquipOutcome.Equipped)
        {
            return CompleteInventoryAction(message, player, outcome, "interact");
        }

        if (!player.InventoryBudget.TrySpend(CurrentServerTick))
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.Throttled, "interact");
        }

        if (HasQuestsFor(npc))
        {
            return BuildNpcDialogue(message, player, npc);
        }

        IReadOnlyList<NpcStockEntry> stock = npc.Definition.Stock;
        VendorStockEntry[] entries = new VendorStockEntry[stock.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new VendorStockEntry(stock[i].ItemKind, stock[i].Price);
        }

        return new AuthoritativeActionResult(
            message.SessionId.Value,
            message.ActionSequence,
            CurrentServerTick,
            Accepted: true,
            Payload: new VendorOpenedResult(npc.ActorId, npc.Definition.DisplayName, entries));
    }

    private AuthoritativeActionResult ProcessVendorBuyAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        VendorBuyIntent intent)
    {
        InventoryEquipOutcome outcome = TryResolveVendor(player, intent.NpcActorId, out SpawnedNpc npc);
        if (outcome == InventoryEquipOutcome.Equipped)
        {
            long price = 0;
            foreach (NpcStockEntry entry in npc.Definition.Stock)
            {
                if (string.Equals(entry.ItemKind, intent.ItemKind, StringComparison.OrdinalIgnoreCase))
                {
                    price = entry.Price;
                    break;
                }
            }

            outcome = price <= 0
                ? InventoryEquipOutcome.NotInStock
                : RunInventoryAction(
                    player,
                    static (service, key, buy) => service.TryBuy(key, buy.ItemKind, buy.Price),
                    (ItemKind: intent.ItemKind, Price: price));
        }

        return CompleteInventoryAction(message, player, outcome, "buy");
    }

    private AuthoritativeActionResult ProcessVendorSellAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        VendorSellIntent intent)
    {
        InventoryEquipOutcome outcome = TryResolveVendor(player, intent.NpcActorId, out _);
        if (outcome == InventoryEquipOutcome.Equipped)
        {
            outcome = RunInventoryAction(
                player,
                static (service, key, sell) => service.TrySell(key, sell.SlotIndex, sell.ItemInstanceId),
                intent);
        }

        return CompleteInventoryAction(message, player, outcome, "sell");
    }
}
