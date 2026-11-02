using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;

namespace Dream.Rendering.Effects;

// Feeds combat events to the effects simulation, the same shape as CombatFeedbackSystem: the
// anchor is read inside the handler, while the actor is still resolvable.
public sealed class FxEventBridge : IDisposable
{
    private readonly FxSimulation _simulation;
    private readonly FxFlashList _flashes;
    private readonly IDisposable _damageSubscription;
    private readonly IDisposable _deathSubscription;

    public FxEventBridge(SceneEventHub events, FxSimulation simulation, FxFlashList flashes)
    {
        ArgumentNullException.ThrowIfNull(events);
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _flashes = flashes ?? throw new ArgumentNullException(nameof(flashes));
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

        Vector3 anchor = evt.Actor.GetWorldAnchor();
        _simulation.SpawnHit(anchor, evt.Kind);
        FxFlashKind flash = evt.Kind switch
        {
            FloatingCombatTextKind.Critical => FxFlashKind.Critical,
            FloatingCombatTextKind.Incoming => FxFlashKind.Incoming,
            _ => FxFlashKind.Hit
        };
        _flashes.Trigger(anchor, flash);
    }

    private void OnActorDied(ActorDiedEvent evt)
    {
        Vector3 anchor = evt.Actor.GetWorldAnchor();
        _simulation.SpawnDeath(anchor);
        _flashes.Trigger(anchor, FxFlashKind.Death);
    }
}
