using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Determinism;
using Dream.Simulation.Inventory;

namespace Dream.Simulation.Runtime;

// A kill's loot reaching the killer (wave I). Loot is rolled on the tick, where the enemy dies, from
// counter seeds only. The notices it produces travel as InventoryPushMessage through the host's
// pending-connection-reply route, never as an action result: kills happen inside the tick golden, which
// hashes every result.
internal readonly record struct SessionInventoryPush(PlayerSessionId SessionId, InventoryPushMessage Message);

public sealed partial class WorldSessionRuntime
{
    // What one session has gained or lost since the host last drained the pushes.
    private sealed class PendingLoot
    {
        public readonly List<LootNotice> Notices = new(InventoryPushMessage.MaxNotices);
        public bool InventoryChanged;
    }

    // At most one entry per session, removed on detach and when the registry forgets the session, and
    // at most MaxNotices notices each, so a host that never drains cannot grow it.
    private readonly Dictionary<PlayerSessionId, PendingLoot> _pendingLoot = new();

    // Content the loot reads its enemy templates and tables from; the shipped catalog unless a test
    // sets another. Granted item kinds always come from the shipped catalog (PlayerInventoryService).
    internal ContentCatalog? LootContent { get; set; }

    internal bool HasPendingInventoryPushes => _pendingLoot.Count > 0;

    // Rolls the killed enemy's loot table for the killer and grants it into the killer's backpack.
    // Never blocks the tick: an inventory that is not loaded is skipped (Ensure would load it
    // synchronously, which can flush the write queue), and the grant uses the character key, never the
    // display name. A kill by a dead player or after leaving cannot get here: the action is rejected or
    // the session purged first. A roll happens even when the backpack is full, so nothing downstream
    // depends on free space.
    internal void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick)
    {
        if (!_sessions.TryGet(killer, out ConnectedPlayer? player) || player == null || !player.IsSpawned)
        {
            return;
        }

        string characterKey = player.CharacterKey.Value;
        if (!_inventory.IsLoaded(characterKey))
        {
            NetworkDiagnosticsLog.Write("server.loot.skipped", $"reason=inventory-not-loaded kind={enemyKind}");
            return;
        }

        ContentCatalog content = LootContent ?? ContentCatalog.Shipped;
        if (!content.TryGetEnemy(enemyKind, out EnemyTemplate? template)
            || template.LootTable is not { } tableKey
            || !content.TryGetLootTable(tableKey, out LootTable? table))
        {
            return;
        }

        ulong sourceHash = StableHash.Of(killer.Value);
        ulong sourceId = Seed.FromInt(enemyActorId);
        for (int dropIndex = 0; dropIndex < table.Drops; dropIndex++)
        {
            ItemRollSeed seed = new(SimulationSeed, tick, sourceHash, sourceId, dropIndex);
            if (!LootRoller.TryRoll(table, in seed, out LootDrop drop))
            {
                continue;
            }

            InventoryEquipOutcome outcome = _inventory.TryGrantRolled(characterKey, drop.ItemKind, drop.Rarity, in seed);
            switch (outcome)
            {
                case InventoryEquipOutcome.Equipped:
                    AddLootNotice(killer, drop, lost: false, inventoryChanged: true);
                    break;
                case InventoryEquipOutcome.BackpackFull:
                    AddLootNotice(killer, drop, lost: true, inventoryChanged: false);
                    break;
                default:
                    NetworkDiagnosticsLog.Write("server.loot.skipped", $"reason={outcome} kind={enemyKind}");
                    break;
            }
        }
    }

    private void AddLootNotice(PlayerSessionId session, LootDrop drop, bool lost, bool inventoryChanged)
    {
        if (!_pendingLoot.TryGetValue(session, out PendingLoot? pending))
        {
            pending = new PendingLoot();
            _pendingLoot[session] = pending;
        }

        pending.InventoryChanged |= inventoryChanged;
        if (pending.Notices.Count >= InventoryPushMessage.MaxNotices)
        {
            return;
        }

        string name = ContentCatalog.Shipped.TryGetItem(drop.ItemKind, out ItemDefinition? definition)
            ? definition.DisplayName
            : drop.ItemKind;
        pending.Notices.Add(new LootNotice(name, drop.Rarity, lost));
    }

    // Moves every session's pending loot into destination as one push each (the snapshot is built once
    // per session however many items it gained) and clears the pending state. Called by the host after
    // the tick, in an unspecified session order: each push goes to its own connection.
    internal void DrainInventoryPushesInto(List<SessionInventoryPush> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach ((PlayerSessionId session, PendingLoot pending) in _pendingLoot)
        {
            PlayerInventorySnapshot? snapshot = null;
            if (pending.InventoryChanged
                && _sessions.TryGet(session, out ConnectedPlayer? player)
                && player != null
                && _inventory.IsLoaded(player.CharacterKey.Value))
            {
                snapshot = _inventory.BuildSnapshot(player.CharacterKey.Value);
            }

            destination.Add(new SessionInventoryPush(
                session,
                new InventoryPushMessage(snapshot, pending.Notices.ToArray())));
        }

        _pendingLoot.Clear();
    }
}
