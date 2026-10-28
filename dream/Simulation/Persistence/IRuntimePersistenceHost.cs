using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Simulation.Persistence;

// The runtime state RuntimePersistenceBridge reads and the runtime operations it calls back into,
// kept narrow so the bridge never depends on WorldSessionRuntime itself. The runtime implements it
// explicitly; tests implement it with fakes. Every member is called on the tick (simulation)
// thread except IsDisposed, which BeginPersistedPlayerPrefetch reads from receive threads.
internal interface IRuntimePersistenceHost
{
    // The current authoritative map; replaced by a map rebuild, so read it on every use.
    Map Map { get; }

    PlayerSessionRegistry Sessions { get; }

    // Set once the runtime's dispose has started (thread-safe read).
    bool IsDisposed { get; }

    PlayerInteractionMode GetSessionInteractionMode(PlayerSessionId sessionId);

    // The live position, facing and health of a session's player, when it has a resolvable target.
    bool TryGetPlayerCheckpointState(
        PlayerSessionId sessionId,
        out Vector3 position,
        out Vector3 facing,
        out int currentHealth,
        out int maxHealth);

    // Enemy id allocation and the default spawn (the runtime's enemy partial).
    void ReserveEnemyActorId(int actorId);

    bool TryAllocateEnemyActorId(out int actorId);

    void SpawnInitialWolf();

    // Inventory forwarders (the runtime's inventory partial).
    void RestorePersistedInventoryState();

    bool IsPlayerInventoryLoaded(string playerName);

    void EvictPlayerInventory(string playerName);

    // The world clock's tick count, saved with every non-null checkpoint (WorldClock.WorldTicks).
    long WorldTicks => 0;
}
