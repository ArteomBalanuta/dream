using System.Runtime.CompilerServices;

namespace Dream.Simulation.Stats;

[InlineArray((int)StatId.Count)]
internal struct StatValues
{
    private float _first;
}

/// <summary>One float per <see cref="StatId"/>, stored inline (no heap allocation).</summary>
public struct StatBlock
{
    private StatValues _values;

    public float this[StatId stat]
    {
        get
        {
            if ((uint)stat >= (uint)StatId.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stat));
            }

            return _values[(int)stat];
        }
        set
        {
            if ((uint)stat >= (uint)StatId.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stat));
            }

            _values[(int)stat] = value;
        }
    }
}
