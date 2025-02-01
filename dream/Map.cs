using OpenTK.Mathematics;
using System;

public class Map
{
    public int SideSize { get; } = 10;
    public Cube[,,] Cubes { get; private set; }

    public Map()
    {
        Cubes = new Cube[SideSize, SideSize, SideSize];
        Initialize();
    }

    private void Initialize()
    {
        for (int x = 0; x < SideSize; x++)
        {
            for (int y = 0; y < SideSize; y++)
            {
                for (int z = 0; z < SideSize; z++)
                {
                    // Create a solid floor at y = 0
                    if (y == 0)
                    {
                        Cubes[x, y, z] = new Cube { IsEmpty = false, IsSolid = true };
                    }
                    else
                    {
                        Cubes[x, y, z] = new Cube();
                    }
                }
            }
        }
    }

    public bool IsInBounds(Vector3 pos)
    {
        return pos.X >= 0 && pos.X < SideSize &&
               pos.Y >= 0 && pos.Y < SideSize &&
               pos.Z >= 0 && pos.Z < SideSize;
    }

    public bool IsSolid(Vector3 pos)
    {
        if (IsInBounds(pos))
        {
            return Cubes[(int)pos.X, (int)pos.Y, (int)pos.Z].IsSolid;
        }
        return true; // Treat out-of-bounds as solid
    }

    public bool CheckCollision(Vector3 position, float width, float height)
    {
        // Clamp the player's position to stay within map bounds
        position.X = MathHelper.Clamp(position.X, 0, SideSize - width);
        position.Y = MathHelper.Clamp(position.Y, 0, SideSize - height);
        position.Z = MathHelper.Clamp(position.Z, 0, SideSize - width);

        // Check collision for the player's bounding box
        for (float x = position.X; x < position.X + width; x += 0.5f) // Check in smaller increments
        {
            for (float y = position.Y; y < position.Y + height; y += 0.5f)
            {
                for (float z = position.Z; z < position.Z + width; z += 0.5f)
                {
                    Vector3 blockPosition = new Vector3(
                        (int)Math.Floor(x),
                        (int)Math.Floor(y),
                        (int)Math.Floor(z)
                    );

                    // Check if the block is solid or out of bounds
                    if (!IsInBounds(blockPosition) || IsSolid(blockPosition))
                    {
                        Console.WriteLine($"Collision with block at: ({blockPosition.X}, {blockPosition.Y}, {blockPosition.Z})");
                        return true;
                    }
                }
            }
        }
        return false;
    }
}