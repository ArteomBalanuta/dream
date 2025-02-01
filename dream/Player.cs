using System.Numerics;

public class Player
{
    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; set; } = Vector3.Zero; // Initialize velocity to zero
    public float Speed { get; set; } = 2.0f; // Speed in blocks per second

    // Player dimensions
    public float Width { get; } = 1.0f; // 1 block wide
    public float Height { get; } = 2.0f; // 2 blocks tall

    public void Update(float deltaTime)
    {
        // Update position based on velocity and deltaTime
        Position += Velocity * Speed * deltaTime;
    }
}