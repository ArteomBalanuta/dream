using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
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

    // The formulas an enemy's damage and attack interval are derived with (EnemyFactory).
    // Defaults to the built-in formulas so test fakes need not provide them.
    CombatFormulaService CombatFormulas => CombatFormulaService.Default;

    // Enemy actor id allocation and the default spawn (the runtime's enemy partial).
    bool TryAllocateEnemyActorId(out int actorId);

    void SpawnInitialWolf();

    // Where players join and respawn; camp members keep out of the aggro range of it. Defaults to the
    // runtime's usual start so test fakes need not provide it.
    Vector3 WorldSpawn => new(5f, 1.5f, 5f);

    // Inventory forwarders (the runtime's inventory partial).
    bool IsPlayerInventoryLoaded(string playerName);

    void EvictPlayerInventory(string playerName);

    // The world clock's tick count, saved with every non-null checkpoint (WorldClock.WorldTicks).
    long WorldTicks => 0;

    // The live mana of a session's player, saved as current_energy (M4); 0 when it has no resolvable
    // target. Defaults to 0 so test fakes need not provide it.
    int GetSessionMana(PlayerSessionId sessionId) => 0;
}
