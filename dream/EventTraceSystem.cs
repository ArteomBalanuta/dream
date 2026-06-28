public sealed class EventTraceSystem : IDisposable
{
    private readonly IDisposable _damageAppliedSubscription;
    private readonly IDisposable _actorDiedSubscription;
    private readonly int _maxEntries;

    public List<string> Entries { get; } = [];

    public EventTraceSystem(SceneEventHub hub, int maxEntries = 32)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _damageAppliedSubscription = hub.Subscribe<DamageAppliedEvent>(_ => AddEntry(nameof(DamageAppliedEvent)));
        _actorDiedSubscription = hub.Subscribe<ActorDiedEvent>(_ => AddEntry(nameof(ActorDiedEvent)));
    }

    public void Dispose()
    {
        _damageAppliedSubscription.Dispose();
        _actorDiedSubscription.Dispose();
    }

    private void AddEntry(string name)
    {
        Entries.Add(name);
        if (Entries.Count > _maxEntries)
        {
            Entries.RemoveAt(0);
        }
    }
}
