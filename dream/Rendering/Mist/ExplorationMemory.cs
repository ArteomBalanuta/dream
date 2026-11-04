namespace Dream.Rendering.Mist;

// What the player has seen of the map, one byte per cell (0 unseen, 255 fully remembered), kept in
// memory only: it is not saved and not sent anywhere. Revealing stamps a disc around the player
// whose edge is soft (full to 8 m, nothing from 10 m), and only ever raises a cell. The dirty flag
// tells ExplorationTexture to upload; nothing here allocates after construction (Reset allocates
// only when the map size changes).
public sealed class ExplorationMemory
{
    public const float FullRadius = 8f;
    public const float OuterRadius = 10f;

    // The player must have moved this far since the last reveal for a new one to run.
    public const float MinMoveMetres = 0.5f;

    private byte[] _cells;
    private float _lastX;
    private float _lastZ;
    private bool _hasLast;

    public ExplorationMemory(int width, int depth)
    {
        _cells = [];
        Width = 0;
        Depth = 0;
        Reset(width, depth);
    }

    public int Width { get; private set; }

    public int Depth { get; private set; }

    // Row-major, z * Width + x; the texture's rows are z.
    public byte[] Cells => _cells;

    public bool Dirty { get; private set; }

    public void ClearDirty() => Dirty = false;

    // A new map (instance or size): forget everything. The buffer is reused when the size is the same.
    public void Reset(int width, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        if (width * depth != _cells.Length)
        {
            _cells = new byte[width * depth];
        }
        else
        {
            Array.Clear(_cells);
        }

        Width = width;
        Depth = depth;
        _hasLast = false;
        Dirty = true;
    }

    // Marks the whole map remembered (the DREAM_WAR_MIST_EXPLORED=all capture setting).
    public void RevealAll()
    {
        Array.Fill(_cells, byte.MaxValue);
        Dirty = true;
    }

    // Stamps the disc around (x, z), a world position in metres, if the player has moved at least
    // MinMoveMetres since the last stamp (the first call always stamps). Returns whether it ran.
    public bool Reveal(float x, float z)
    {
        if (_hasLast)
        {
            float dx = x - _lastX;
            float dz = z - _lastZ;
            if (dx * dx + dz * dz < MinMoveMetres * MinMoveMetres)
            {
                return false;
            }
        }

        _hasLast = true;
        _lastX = x;
        _lastZ = z;

        int minX = Math.Max(0, (int)MathF.Floor(x - OuterRadius));
        int maxX = Math.Min(Width - 1, (int)MathF.Floor(x + OuterRadius));
        int minZ = Math.Max(0, (int)MathF.Floor(z - OuterRadius));
        int maxZ = Math.Min(Depth - 1, (int)MathF.Floor(z + OuterRadius));
        for (int cellZ = minZ; cellZ <= maxZ; cellZ++)
        {
            float dz = cellZ + 0.5f - z;
            int row = cellZ * Width;
            for (int cellX = minX; cellX <= maxX; cellX++)
            {
                float dx = cellX + 0.5f - x;
                float distance = MathF.Sqrt(dx * dx + dz * dz);
                if (distance >= OuterRadius)
                {
                    continue;
                }

                byte value = (byte)MathF.Round(255f * (1f - SmoothStep(FullRadius, OuterRadius, distance)));
                if (value > _cells[row + cellX])
                {
                    _cells[row + cellX] = value;
                    Dirty = true;
                }
            }
        }

        return true;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
