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

        Cubes[3, 9, 2] = new Cube { IsEmpty = false, IsSolid = true, IsFire = false, Animation = new AnimatedModel("D:\\workspace\\dream-project\\dream\\animations\\ak-15.gltf"), ShaderName = "ak_shader" };
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
        // Calculate the player's collision box bounds
        float halfWidth = width / 2;
        float minX = position.X - halfWidth;
        float maxX = position.X + halfWidth;
        float minZ = position.Z - halfWidth;
        float maxZ = position.Z + halfWidth;
        float minY = position.Y - height / 2;
        float maxY = position.Y + height / 2;

        // Check all blocks overlapping with the player's collision box
        for (float x = minX; x <= maxX; x += 0.5f)
        {
            for (float z = minZ; z <= maxZ; z += 0.5f)
            {
                for (float y = minY; y <= maxY; y += 0.5f)
                {
                    int blockX = (int)Math.Floor(x);
                    int blockY = (int)Math.Floor(y);
                    int blockZ = (int)Math.Floor(z);

                    // Check if the block is solid or out of bounds
                    if (blockX < 0 || blockX >= SideSize ||
                        blockY < 0 || blockY >= SideSize ||
                        blockZ < 0 || blockZ >= SideSize)
                    {
                        // Treat out-of-bounds as solid
                        return true;
                    }

                    if (Cubes[blockX, blockY, blockZ].IsSolid)
                    {
                        Console.WriteLine($"Collision with block at: ({blockX}, {blockY}, {blockZ})");
                        return true;
                    }
                }
            }
        }
        return false;
    }
}