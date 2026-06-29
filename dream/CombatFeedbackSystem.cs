public sealed class CombatFeedbackSystem : IDisposable
{
    private readonly FloatingCombatTextSystem _floatingText;
    private readonly IDisposable _damageSubscription;

    public CombatFeedbackSystem(SceneEventHub events, FloatingCombatTextSystem floatingText)
    {
        _floatingText = floatingText;
        _damageSubscription = events.Subscribe<DamageAppliedEvent>(OnDamageApplied);
    }

    public void Update(float deltaTime)
    {
        _floatingText.Update(deltaTime);
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

        _floatingText.Spawn(evt.Actor.Id, evt.Actor.GetWorldAnchor(), evt.Amount, evt.Kind);
    }
}
