using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;

namespace Dream.Rendering.Effects;

// Feeds combat events to the effects simulation, the same shape as CombatFeedbackSystem: the
// anchor is read inside the handler, while the actor is still resolvable.
public sealed class FxEventBridge : IDisposable
{
    private readonly FxSimulation _simulation;
    private readonly IDisposable _damageSubscription;
    private readonly IDisposable _deathSubscription;

    public FxEventBridge(SceneEventHub events, FxSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(events);
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _damageSubscription = events.Subscribe<DamageAppliedEvent>(OnDamageApplied);
        _deathSubscription = events.Subscribe<ActorDiedEvent>(OnActorDied);
    }

    public void Dispose()
    {
        _damageSubscription.Dispose();
        _deathSubscription.Dispose();
    }

    private void OnDamageApplied(DamageAppliedEvent evt)
    {
        if (evt.Amount <= 0)
        {
            return;
        }

        _simulation.SpawnHit(evt.Actor.GetWorldAnchor(), evt.Kind);
    }

    private void OnActorDied(ActorDiedEvent evt)
    {
        _simulation.SpawnDeath(evt.Actor.GetWorldAnchor());
    }
}
