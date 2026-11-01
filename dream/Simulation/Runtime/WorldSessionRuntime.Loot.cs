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

    internal void AddLootNotice(PlayerSessionId session, LootDrop drop, bool lost, bool inventoryChanged)
    {
        if (!_pendingLoot.TryGetValue(session, out PendingLoot? pending))
        {
            pending = new PendingLoot();
            _pendingLoot[session] = pending;
        }

        pending.InventoryChanged |= inventoryChanged;
        string name = ContentCatalog.Shipped.TryGetItem(drop.ItemKind, out ItemDefinition? definition)
            ? definition.DisplayName
            : drop.ItemKind;
        LootNotice notice = new(name, drop.Rarity, lost);
        if (pending.Notices.Count < InventoryPushMessage.MaxNotices)
        {
            pending.Notices.Add(notice);
            return;
        }

        // Full: a lost notice (the player must learn the backpack overflowed) replaces the LAST ordinary
        // one, because the client shows only the tail of a push (LootFeedbackSystem.MaxLines); anything
        // else is dropped. Either way the drop is logged. Only reached past the cap.
        string? replaced = null;
        if (lost)
        {
            for (int i = pending.Notices.Count - 1; i >= 0; i--)
            {
                if (!pending.Notices[i].Lost)
                {
                    replaced = pending.Notices[i].ItemDisplayName;
                    pending.Notices[i] = notice;
                    break;
                }
            }
        }

        NetworkDiagnosticsLog.WriteEvery(
            "server.loot.notice-dropped",
            NoticeDroppedLogInterval,
            "server.loot.notice-dropped",
            (session.Value, drop.ItemKind, lost, replaced),
            static state => $"session={state.Value} item={state.ItemKind} lost={state.lost} replaced={state.replaced ?? "none"}");
    }

    private static readonly TimeSpan NoticeDroppedLogInterval = TimeSpan.FromMilliseconds(200);

    // Moves every session's pending loot into destination as one push each (the snapshot is built once
    // per session however many items it gained) and clears the pending state. Called by the host after
    // the tick, in an unspecified session order: each push goes to its own connection. A session whose
    // id is in heldSessionIds keeps its pending loot for a later drain (the host holds a push back
    // while an older inventory result of that session is still queued); other sessions drain as usual.
    internal void DrainInventoryPushesInto(
        List<SessionInventoryPush> destination,
        HashSet<string>? heldSessionIds = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        List<PlayerSessionId>? drained = null;
        foreach ((PlayerSessionId session, PendingLoot pending) in _pendingLoot)
        {
            if (heldSessionIds != null && heldSessionIds.Contains(session.Value))
            {
                continue;
            }

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
            if (heldSessionIds != null && heldSessionIds.Count > 0)
            {
                (drained ??= new List<PlayerSessionId>()).Add(session);
            }
        }

        if (heldSessionIds == null || heldSessionIds.Count == 0)
        {
            _pendingLoot.Clear();
            return;
        }

        if (drained != null)
        {
            for (int i = 0; i < drained.Count; i++)
            {
                _pendingLoot.Remove(drained[i]);
            }
        }
    }
}
