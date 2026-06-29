public static class ClientSnapshotDamageApplier
{
    public static void ApplyToRuntime(
        WorldSessionRuntime runtime,
        SceneEventHub events,
        ServerSnapshotEnvelope snapshot)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(events);

        foreach (DamageEventDelta damageEvent in snapshot.DamageEvents)
        {
            if (damageEvent.Amount <= 0)
            {
                continue;
            }

            if (runtime.TryResolveCombatActor(damageEvent.ActorId, out CombatActorHandle actor))
            {
                events.Publish(new DamageAppliedEvent(actor, damageEvent.Amount, FloatingCombatTextKind.Regular));
            }
        }
    }
}
