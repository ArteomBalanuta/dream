using Dream.Simulation.Inventory;
using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;

// M3 (protocol v16). One item lying on the ground. ClaimantActorId and ClaimExpiresTick are 0 and 0
// for a free-for-all item; otherwise only the claimant's player may pick it up until the tick.
public readonly record struct GroundItemSnapshot(
    long ItemInstanceId,
    string ItemKind,
    ItemRarity Rarity,
    Vector3 Position,
    int ClaimantActorId,
    ulong ClaimExpiresTick);

// M3 (protocol v16). An unprompted, server-only message with the ground items a client should show:
// Reset true replaces the client's whole list with Upserts (sent first, on join and whenever a client
// must resync); otherwise Upserts add or replace entries by item id and RemovedIds drop them. Both are
// idempotent. A larger set is split into messages of at most MaxEntries entries, Reset only on the
// first. It travels outside AuthoritativeActionResult so tick goldens, which hash every action
// result, do not move.
public sealed record ServerGroundItems(
    bool Reset,
    IReadOnlyList<GroundItemSnapshot> Upserts,
    IReadOnlyList<long> RemovedIds) : IServerToClientMessage
{
    public const string Kind = "server.ground-items";

    public const int MaxEntries = 64;

    public string MessageKind => Kind;
}
