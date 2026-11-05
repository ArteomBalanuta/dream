namespace Dream.Multiplayer.Protocol;

public readonly record struct TransportEnvelope(
    int ProtocolVersion,
    string MessageKind,
    ulong Sequence)
{
    // Version 2 leaves null properties and derived properties out of payloads. Version 3 (SCALE-1)
    // encodes worlds compactly (WorldSnapshotJsonConverter) and sends join and resync worlds as
    // chunked transfers (WorldChunkMessage). Version 4 (SCALE-2) sends snapshot actors as
    // delta-coded frames ("actorFrame" in place of "actors") and moves each connection's accepted
    // command sequence into the envelope header (the Ack below); a v3 peer would find no "actors"
    // list and fail with a NullReferenceException, so the bump makes both sides refuse it with a
    // ProtocolVersionMismatchException instead. Version 5 (T1) adds the world clock anchor to the
    // join-accepted message (JoinAcceptedMessage.WorldClock); a v4 client would ignore it and show a
    // fixed time of day, so both sides refuse it instead. Version 6 (T2f) adds each player actor's
    // held light (ActorSnapshot.HeldLight, the Equipment group of an actor frame record, mask bit
    // 256); a v5 client would reject the mask, so both sides refuse it. Version 7 (persistence
    // wave B) adds the world descriptor to the join-accepted message (JoinAcceptedMessage.World): a
    // v6 client would size its map from the first snapshot and ignore the descriptor, so both sides
    // refuse it. Version 8 (persistence wave E) adds the stored-terrain manifest
    // (JoinAcceptedMessage.Terrain) and the server.terrain-chunk transfer of stored worlds; a v7 client could not
    // build a stored world, so both sides refuse it. Version 9 (persistence wave G) adds the optional
    // accountToken to the join request (ClientJoinRequestMessage.AccountToken); a v8 client could not
    // identify its account, so both sides refuse it. Version 10 (persistence wave H) adds the
    // inventory unequip, move and destroy intents and the equip intent's target slot, and the stats,
    // light slots, rarity and equipment slots of an inventory snapshot's items (InventoryItemSnapshot,
    // PlayerInventorySnapshot.Equipment); a v9 client would show no gear and could not use the new
    // slots, so both sides refuse it. Actor snapshots are unchanged apart from this envelope's version
    // digit. Version 11 (persistence wave I) adds the server.inventory-push message (loot notices and
    // a fresh inventory snapshot) and the optional EnemyPresentation actor group (enemy level and
    // visual key); a v10 client would fail on the group mask, so both sides refuse it. A wolf-only
    // world's actor bytes are unchanged apart from the version digit. Version 12 (classic HUD) adds the
    // optional trailing "hit" field of a damage event (DamageEventDelta.HitKind, written only for a
    // critical or incoming hit); a v11 client would show every number as a regular hit, so both sides
    // refuse it. Snapshots without a critical hit are unchanged apart from the version digit. Version
    // 13 (classic HUD iteration 2) writes the inventory snapshot compactly
    // (PlayerInventorySnapshotJsonConverter: "bag" and "worn" entry arrays, item text rehydrated from
    // the shipped content catalog) for an 8x8 footprint bag and twelve worn slots, adds the optional
    // "targetCell" of the unequip intent, and widens the equip, move and destroy intents' slot indexes
    // (bag anchors 0..63, worn slots 100..111); a v12 client would misread the inventory, so both sides
    // refuse it. Actor and world snapshots are unchanged apart from the version digit. Version 14
    // (multiplayer M1) adds the chat messages (client.chat, server.chat) and the character list
    // (client.character-list, server.character-list); a v13 peer would fail on the unknown kinds, so
    // both sides refuse it. Existing payloads are unchanged apart from the version digit. Version 15
    // (stat points, M2-A) adds the "player.stats.spend" action (PlayerStatSpendIntent). Version
    // 16 (ground items, gold and vendors, M3) adds the server.ground-items message, the "ground.pickup",
    // "npc.interact", "vendor.buy" and "vendor.sell" actions, the "vendor.opened" action result, the
    // optional "gold" field of the inventory snapshot (written only above 0) and the Npc creature kind
    // of an actor snapshot; a v15 peer would fail on the unknown kinds, so both sides refuse it.
    // Version 17 (quickbar and mana, M4) adds the "skill.use", "item.use" and "quickbar.bind" actions,
    // the "ability.used" action result, the server.vitals message and the optional "quick" field of the
    // inventory snapshot (written only when the character has a bar); a v16 peer would fail on the
    // unknown kinds, so both sides refuse it. Version 18 (party, trade, quests and admin, M5, M6 and M8) adds twelve
    // player actions, the "npc.dialogue" action result, the Party and Command chat channels and the
    // server.party, server.trade, server.quest-log and server.progress messages, and stops sending other
    // players' experience and stats in actor snapshots; a v17 peer would fail on the unknown kinds, so both sides refuse it.
    // v19 merges the stats sheet: combat group [str, as, vit, agi, energy], player VisualKey main|off,
    // agility/energy in ServerPlayerProgress.BaseStats; a v18 peer would misread the combat group, so
    // both sides refuse it.
    // Version 20 adds the "inventory.drop" action (InventoryDropIntent): a bag item dragged out of the
    // inventory window becomes a ground item at the dropper's own position. No message kind and no
    // payload changes; a v19 peer would fail on the unknown action, so both sides refuse it.
    // Version 21 (worn equipment visuals) widens the player VisualKey from "main|off" to nine
    // positional fields "main|off|head|body|hands|feet|legs|back|companion", with trailing empty
    // fields trimmed (a player wearing nothing still sends exactly "main|off") and Neck and the two
    // ring slots deliberately absent - at the shipped isometric zoom the whole figure is 28x101
    // pixels and a ring is under 2; a v20 peer reads a worn key's extra separators as a malformed
    // key and draws an armoured player bare-handed, so both sides refuse it. Every other payload is
    // unchanged apart from the version digit.
    //   20 and 21 were developed in parallel and BOTH waves originally bumped 19 -> 20; the worn
    //   wave moved to 21 before either merged. Mismatched peers refuse each other by digit alone, so
    //   two meanings for one digit is a disagreement neither side could ever detect - which is what
    //   this comment exists to prevent. Before claiming a version, check the unmerged branches:
    //   git show <branch>:dream/Multiplayer/Protocol/TransportEnvelope.cs | grep CurrentProtocolVersion
    // Decode rejects any other version.
    public const int CurrentProtocolVersion = 21;

    // SCALE-2: the receiving connection's own last accepted command sequence, written in the
    // envelope header ("ack") ahead of a shared snapshot payload (ServerSnapshotMessage.SharedBody)
    // so the payload itself stays identical for every connection. Null on every other frame.
    public TransportAck? Ack { get; init; }
}

// One session's accepted command sequence. SessionId is null when the session has no accepted
// command yet; the header then carries an empty object, so the client still gets a (empty)
// sequence map.
public readonly record struct TransportAck(string? SessionId, ulong Sequence);
