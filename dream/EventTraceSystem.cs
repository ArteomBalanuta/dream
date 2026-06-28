public sealed class EventTraceSystem : IDisposable
{
    private readonly IDisposable _damageAppliedSubscription;
    private readonly IDisposable _actorDiedSubscription;

    public List<string> Entries { get; } = [];

    public EventTraceSystem(SceneEventHub hub)
    {
        _damageAppliedSubscription = hub.Subscribe<DamageAppliedEvent>(_ => Entries.Add(nameof(DamageAppliedEvent)));
        _actorDiedSubscription = hub.Subscribe<ActorDiedEvent>(_ => Entries.Add(nameof(ActorDiedEvent)));
    }

    public void Dispose()
    {
        _damageAppliedSubscription.Dispose();
        _actorDiedSubscription.Dispose();
    }
}
