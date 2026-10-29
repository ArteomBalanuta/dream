using System.Globalization;

namespace Dream.Simulation.World;

// The identity of one persisted world (worlds.world_id). Ids start at 1 and are never reused.
// World 1 is the world every database had before worlds became rows. The id never enters
// simulation state, checksums or snapshots.
public readonly record struct WorldId
{
    public static readonly WorldId Default = new(1);

    public WorldId(long value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1L);
        Value = value;
    }

    public long Value { get; }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
