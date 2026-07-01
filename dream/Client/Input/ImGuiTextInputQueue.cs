namespace Dream.Client.Input;

public sealed class ImGuiTextInputQueue
{
    private readonly Queue<uint> _characters = new();

    public int Count => _characters.Count;

    public void Enqueue(uint unicode)
    {
        if (unicode == 0)
        {
            return;
        }

        _characters.Enqueue(unicode);
    }

    public IReadOnlyList<uint> Drain()
    {
        if (_characters.Count == 0)
        {
            return Array.Empty<uint>();
        }

        uint[] drained = _characters.ToArray();
        _characters.Clear();
        return drained;
    }
}
