namespace Dream.Rendering.Lighting;

// Filled by the scene each frame before DrawWorld; never allocates after construction.
public sealed class LightCollector
{
    public const int Capacity = 1024;

    private readonly PointLight[] _lights = new PointLight[Capacity];

    public int Count { get; private set; }

    public int Dropped { get; private set; }

    public ReadOnlySpan<PointLight> Lights => _lights.AsSpan(0, Count);

    public void Clear()
    {
        Count = 0;
        Dropped = 0;
    }

    public void Add(in PointLight light)
    {
        if (!(light.Radius > 0f) || !float.IsFinite(light.Radius) || !float.IsFinite(light.Position.X)
            || !float.IsFinite(light.Position.Y) || !float.IsFinite(light.Position.Z))
        {
            return;
        }

        if (Count == Capacity)
        {
            Dropped++;
            return;
        }

        _lights[Count++] = light;
    }
}
