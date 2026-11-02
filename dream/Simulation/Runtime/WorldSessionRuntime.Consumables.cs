using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;

namespace Dream.Simulation.Runtime;

// One session's mana and cooldowns for the owner's ServerPlayerVitals; DedicatedServerHost turns each
// into a message for the session's connection.
internal readonly record struct SessionVitals(PlayerSessionId SessionId, ServerPlayerVitals Message);

// Consumables, the quickbar, mana regeneration and the vitals drain (M4 workstream A).
//
// Item use returns InventoryChangedResult through CompleteInventoryAction, never a payload of its own:
// the host holds a loot push back only while an InventoryChangedResult of that session is queued, so
// any other carrier could be overtaken by an older inventory. Mana and cooldowns reach the owner as
// ServerPlayerVitals, outside the action results.
public sealed partial class WorldSessionRuntime
{
    // The vitals drain runs once per this many ticks: up to 250 ms of HUD latency.
    internal const int VitalsIntervalTicks = 16;

    private AuthoritativeActionResult ProcessItemUseAction(
        PlayerActionMessage message,
        AuthoritativeSessionActionContext context,
        ItemUseIntent intent)
    {
        ConnectedPlayer player = context.Player;
        PlayerCombatant combatant = context.Combatant;
        string characterKey = player.CharacterKey.Value;
        ulong tick = CurrentServerTick;

        InventoryEquipOutcome outcome = _inventory.TryPeekConsumable(
            characterKey,
            intent.SlotIndex,
            intent.ItemInstanceId,
            out ItemUseDefinition use,
            out string kind);
        if (outcome != InventoryEquipOutcome.Equipped)
        {
            return CompleteInventoryAction(message, player, outcome, "use");
        }

        if (!player.Cooldowns.IsReady(kind, tick))
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.OnCooldown, "use");
        }

        bool restoresHealth = use.RestoreHealth > 0 && combatant.Health.CurrentHealth < combatant.Health.MaxHealth;
        bool restoresMana = use.RestoreMana > 0 && combatant.CurrentMana < combatant.MaxMana;
        if (!restoresHealth && !restoresMana)
        {
            return CompleteInventoryAction(message, player, InventoryEquipOutcome.NothingToRestore, "use");
        }

        outcome = RunInventoryAction(
            player,
            static (service, key, i) => service.TryConsume(key, i.SlotIndex, i.ItemInstanceId),
            intent);
        if (outcome == InventoryEquipOutcome.Equipped)
        {
            combatant.RestoreHealth(use.RestoreHealth);
            combatant.RestoreMana(use.RestoreMana);
            player.Cooldowns.Start(kind, tick + (ulong)Math.Max(0, use.CooldownTicks), tick);
            player.VitalsDirty = true;
        }

        return CompleteInventoryAction(message, player, outcome, "use");
    }

    private AuthoritativeActionResult ProcessQuickbarBindAction(
        PlayerActionMessage message,
        ConnectedPlayer player,
        QuickbarBindIntent intent)
    {
        InventoryEquipOutcome outcome = RunInventoryAction(
            player,
            static (service, key, i) => service.TryBindQuickbar(key, i.Slot, i.Kind, i.Key),
            intent);
        return CompleteInventoryAction(message, player, outcome, "bind");
    }

    // Once per gameplay tick, after AdvanceRespawns. On a regeneration tick, every spawned remote player
    // below its maximum and alive gains PlayerManaRules.RegenAmount. The primary seat counts only with a
    // remote occupant (the unoccupied local seat is not a player). Allocation-free; a steady tick reads
    // one modulus.
    private void AdvanceMana()
    {
        if (CurrentServerTick % PlayerManaRules.RegenIntervalTicks != 0)
        {
            return;
        }

        PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
        if (primarySessionId != LocalPlayerSessionId)
        {
            RegenerateMana(primarySessionId, Player.Combatant);
        }

        RemoteSessionPlayerState[] secondaries = _orderedSecondaryRemotePlayers;
        for (int i = 0; i < secondaries.Length; i++)
        {
            RegenerateMana(secondaries[i].SessionId, secondaries[i].Combatant);
        }
    }

    private void RegenerateMana(PlayerSessionId sessionId, PlayerCombatant combatant)
    {
        if (combatant.CurrentMana >= combatant.MaxMana
            || !combatant.Health.IsAlive
            || !_sessions.TryGet(sessionId, out ConnectedPlayer? player)
            || player == null
            || !player.IsSpawned)
        {
            return;
        }

        combatant.RestoreMana(PlayerManaRules.RegenAmount(combatant.Level));
    }

    // True on the ticks the host should call DrainVitalsInto.
    internal bool IsVitalsDrainTick => CurrentServerTick % VitalsIntervalTicks == 0;

    // Appends a message for each spawned remote player whose vitals changed since the last one sent:
    // VitalsDirty, or mana or the maximum differing from what the owner last received. Only on a drain
    // tick; otherwise nothing. isClaimed decides whether the owner has a connection: a session without
    // one keeps its dirty state so the first message after the claim carries everything.
    internal void DrainVitalsInto(List<SessionVitals> into, Func<PlayerSessionId, bool>? isClaimed = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!IsVitalsDrainTick)
        {
            return;
        }

        PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
        if (primarySessionId != LocalPlayerSessionId)
        {
            CollectVitals(primarySessionId, Player.Combatant, into, isClaimed);
        }

        RemoteSessionPlayerState[] secondaries = _orderedSecondaryRemotePlayers;
        for (int i = 0; i < secondaries.Length; i++)
        {
            CollectVitals(secondaries[i].SessionId, secondaries[i].Combatant, into, isClaimed);
        }
    }

    private void CollectVitals(
        PlayerSessionId sessionId,
        PlayerCombatant combatant,
        List<SessionVitals> into,
        Func<PlayerSessionId, bool>? isClaimed)
    {
        if (!_sessions.TryGet(sessionId, out ConnectedPlayer? player)
            || player == null
            || !player.IsSpawned
            || (!player.VitalsDirty
                && player.LastSentMana == combatant.CurrentMana
                && player.LastSentMaxMana == combatant.MaxMana)
            || (isClaimed != null && !isClaimed(sessionId)))
        {
            return;
        }

        List<AbilityCooldown> running = [];
        player.Cooldowns.CopyRunning(CurrentServerTick, running);
        if (running.Count > ServerPlayerVitals.MaxCooldowns)
        {
            running.RemoveRange(ServerPlayerVitals.MaxCooldowns, running.Count - ServerPlayerVitals.MaxCooldowns);
        }

        into.Add(new SessionVitals(
            sessionId,
            new ServerPlayerVitals(combatant.CurrentMana, combatant.MaxMana, running)));
        player.LastSentMana = combatant.CurrentMana;
        player.LastSentMaxMana = combatant.MaxMana;
        player.VitalsDirty = false;
    }
}
