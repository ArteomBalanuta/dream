public sealed class SceneEventHub
{
    private readonly Dictionary<Type, List<Delegate>> _subscriptions = new();

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        Type eventType = typeof(TEvent);
        if (!_subscriptions.TryGetValue(eventType, out List<Delegate>? handlers))
        {
            handlers = [];
            _subscriptions[eventType] = handlers;
        }

        handlers.Add(handler);
        return new Subscription(() => Unsubscribe(eventType, handler));
    }

    public void Publish<TEvent>(TEvent eventData)
    {
        if (!_subscriptions.TryGetValue(typeof(TEvent), out List<Delegate>? handlers))
        {
            return;
        }

        Delegate[] snapshot = handlers.ToArray();
        foreach (Delegate handler in snapshot)
        {
            ((Action<TEvent>)handler)(eventData);
        }
    }

    private void Unsubscribe(Type eventType, Delegate handler)
    {
        if (!_subscriptions.TryGetValue(eventType, out List<Delegate>? handlers))
        {
            return;
        }

        handlers.Remove(handler);
        if (handlers.Count == 0)
        {
            _subscriptions.Remove(eventType);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private readonly Action _dispose = dispose;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dispose();
        }
    }
}
