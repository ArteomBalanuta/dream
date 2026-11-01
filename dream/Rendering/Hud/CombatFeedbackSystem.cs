using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;

namespace Dream.Rendering.Hud;

public sealed class CombatFeedbackSystem : IDisposable
{
    private readonly FloatingCombatTextSystem _floatingText;
    private readonly IDisposable _damageSubscription;
    private readonly Func<int>? _localActorId;

    // localActorId, when given, names the local player's actor: damage that lands on it is shown as
    // Incoming, whatever kind the event carried.
    public CombatFeedbackSystem(SceneEventHub events, FloatingCombatTextSystem floatingText, Func<int>? localActorId = null)
    {
        _floatingText = floatingText;
        _localActorId = localActorId;
        _damageSubscription = events.Subscribe<DamageAppliedEvent>(OnDamageApplied);
    }

    public void Update(float deltaTime)
    {
        Update(deltaTime, FloatingCombatTextTuning.Default);
    }

    public void Update(float deltaTime, FloatingCombatTextTuning tuning)
    {
        _floatingText.Update(deltaTime, tuning);
    }

    public void CollectRenderStates(List<FloatingCombatTextRenderState> destination)
    {
        _floatingText.CollectRenderStates(destination);
    }

    public void CollectRenderStates(List<FloatingCombatTextRenderState> destination, FloatingCombatTextTuning tuning)
    {
        _floatingText.CollectRenderStates(destination, tuning);
    }

    public void Dispose()
    {
        _damageSubscription.Dispose();
    }

    private void OnDamageApplied(DamageAppliedEvent evt)
    {
        if (evt.Amount <= 0)
        {
            return;
        }

        FloatingCombatTextKind kind = _localActorId is not null && evt.Actor.Id == _localActorId()
            ? FloatingCombatTextKind.Incoming
            : evt.Kind;
        _floatingText.Spawn(evt.Actor.Id, evt.Actor.GetWorldAnchor(), evt.Amount, kind);
    }
}
