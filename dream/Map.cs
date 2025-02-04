using BulletSharp;
using BulletSharp.Math;
using BulletSharp.SoftBody;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Map
{
    public int SideSize { get; } = 30;
    public Cube[,,] Cubes { get; private set; }

    private PhysicsIntegrator physicsIntegrator;

    public Map(PhysicsIntegrator physicsIntegrator)
    {
        this.physicsIntegrator = physicsIntegrator;
        Cubes = new Cube[SideSize, SideSize, SideSize];
        Initialize();
    }

    private void Initialize()
    {
        // First fill the entire array:
        for (int x = 0; x < SideSize; x++)
        {
            for (int y = 0; y < SideSize; y++)
            {
                for (int z = 0; z < SideSize; z++)
                {
                    if (y == 0)
                    {
                        // Floor cell: solid.
                        Cubes[x, y, z] = new Cube { IsEmpty = false, IsSolid = true, Position = new Vector3(x, y, z) };
                    }
                    else
                    {
                        // Other cells: empty by default.
                        Cubes[x, y, z] = new Cube { IsEmpty = true, IsSolid = false, Position = new Vector3(x, y, z) };
                    }
                }
            }
        }

        // Now add physics bodies for every solid cube.
        for (int x = 0; x < SideSize; x++)
        {
            for (int y = 0; y < SideSize; y++)
            {
                for (int z = 0; z < SideSize; z++)
                {
                    Cube cube = Cubes[x, y, z];
                    if (cube != null && cube.IsSolid)
                    {
                        // Create the physics body using your cube's own method.
                        RigidBody body = cube.CreateRigidBody();
                        // Store the grid indices for later updates.
                        body.UserObject = Tuple.Create(x, y, z);
                        this.physicsIntegrator.World.AddRigidBody(body);
                        this.physicsIntegrator.MapCubeBodies.Add(body);
                    }
                }
            }
        }


        Cubes[3, 2, 2] = new Cube { IsEmpty = false, IsSolid = true, IsFire = false, 
            Animation = new AnimatedModel("D:\\workspace\\dream-project\\dream\\animations\\new.gltf"), ShaderName = "cube", Position = new Vector3(3, 3, 2) };

        Cubes[6, 6, 6] = new Cube { IsEmpty = false, IsSolid = true, IsFire = false, 
            Animation = new AnimatedModel("D:\\workspace\\dream-project\\dream\\animations\\new.gltf"), ShaderName = "cube", Position = new Vector3(6, 6, 6) };
        //Cubes[3, 2, 2].Animation.StartAnimation();
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

    public void AddCube(Vector3 position)
    {
        // Con222 vert position to cube grid indices (assuming position is in world coordinates)
        int x = (int)Math.Floor(position.X);
        int y = (int)Math.Floor(position.Y);
        int z = (int)Math.Floor(position.Z);

        if (IsInBounds(new Vector3(x, y, z)) && !Cubes[x, y, z].IsSolid && Cubes[x, y, z].IsEmpty)
        {
            Cube cube = new Cube { IsEmpty = false, IsSolid = true, IsFire = false, Animation = new AnimatedModel("D:\\workspace\\dream-project\\dream\\animations\\new.gltf"), ShaderName = "cube", Position = new Vector3(x, y, z) };
            Cubes[x, y, z] = cube;

            RigidBody body = cube.CreateRigidBody();
            // Store the grid indices for later update.
            body.UserObject = Tuple.Create(x, y, z);
            this.physicsIntegrator.World.AddRigidBody(body);
            this.physicsIntegrator.MapCubeBodies.Add(body);

            Console.WriteLine($"Placed a cube at: ({x}, {y}, {z})");
        }
    }
}