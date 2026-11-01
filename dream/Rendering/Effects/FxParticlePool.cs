using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

public enum FxParticleKind : byte
{
    Spark,
    Ember,
    Dust,
    Ash
}

// One live particle. Color is premultiplied RGBA at full strength; the fade over its life scales
// all four channels. Dust never expires (Life is infinite): the simulation wraps it instead.
public struct FxParticle
{
    public FxParticleKind Kind;
    public Vector3 Position;
    public Vector3 Velocity;
    public float Age;
    public float Life;
    public float StartHalfSize;
    public float EndHalfSize;
    public float Rotation;
    public float AngularVelocity;
    public float Frame;
    public float Gravity;
    public float Drag;
    // Offsets the ember flicker so a burst does not pulse in step.
    public float Phase;
    public Vector4 Color;
}

// A fixed-capacity particle store. Removal swaps the last particle into the hole, so order is not
// stable and nothing is sorted; a full pool drops the spawn and counts it.
public sealed class FxParticlePool
{
    private readonly FxParticle[] _items;

    public FxParticlePool(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = new FxParticle[capacity];
    }

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    public long DroppedSpawns { get; private set; }

    public ref FxParticle this[int index] => ref _items[index];

    public bool TryAdd(in FxParticle particle)
    {
        if (Count == _items.Length)
        {
            DroppedSpawns++;
            return false;
        }

        _items[Count++] = particle;
        return true;
    }

    // Like TryAdd, but a refusal is not counted: for the steady top-up of ambient particles,
    // which would otherwise inflate the counter every frame while the pool is full.
    public bool TryAddQuietly(in FxParticle particle)
    {
        if (Count == _items.Length)
        {
            return false;
        }

        _items[Count++] = particle;
        return true;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        Count--;
        if (index != Count)
        {
            _items[index] = _items[Count];
        }

        _items[Count] = default;
    }

    public void Clear()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
    }
}
