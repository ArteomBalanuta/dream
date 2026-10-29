using System.Globalization;

namespace Dream.Simulation.World;

// The stable, never-reused identity of one persisted world object (placed cube, rubble piece, enemy
// or item). It exists only in persistence: actor ids stay the identity on the wire, in checksums
// and in RNG seeds. Value 0 (None) means "never allocated".
public readonly record struct ObjectId : IComparable<ObjectId>
{
    public static readonly ObjectId None = default;

    public ObjectId(long value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1L);
        Value = value;
    }

    public long Value { get; }

    public bool IsNone => Value == 0;

    public int CompareTo(ObjectId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
