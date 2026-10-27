namespace Dream.Rendering.Actors;


public sealed class EnemyVisualStateCache
{
    private readonly Dictionary<int, EnemyVisualState> _states = new();
    private readonly List<int> _staleIds = new();

    public int Count => _states.Count;

    public EnemyVisualState GetOrAdd(int actorId)
    {
        if (!_states.TryGetValue(actorId, out EnemyVisualState? state))
        {
            state = new EnemyVisualState();
            _states[actorId] = state;
        }

        return state;
    }

    public void PruneExcept(HashSet<int> currentIds)
    {
        _staleIds.Clear();
        foreach (int id in _states.Keys)
        {
            if (!currentIds.Contains(id))
            {
                _staleIds.Add(id);
            }
        }

        foreach (int id in _staleIds)
        {
            _states.Remove(id);
        }
    }
}
