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
        // Check collision for the player's bounding box
        for (float x = position.X; x < position.X + width; x++)
        {
            for (float y = position.Y; y < position.Y + height; y++)
            {
                for (float z = position.Z; z < position.Z + width; z++)
                {
                    Vector3 blockPosition = new Vector3((int)x, (int)y, (int)z);
                    if (IsSolid(blockPosition))
                    {
                        // Log the block that caused the collision
                        Console.WriteLine($"Collision with block at: ({blockPosition.X}, {blockPosition.Y}, {blockPosition.Z})");
                        return true; // Collision detected
                    }
                }
            }
        }
        return false; // No collision
    }
}