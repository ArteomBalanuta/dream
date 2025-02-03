

using OpenTK.Mathematics;

public class Cube
{
    public Vector3 Position { get; set; }
    public int Side { get; } = 1;
    public bool IsEmpty { get; set; } = true;
    public bool IsSolid { get; set; } = false;
    public bool IsFire { get; set; } = false;
    public AnimatedModel Animation { get; set; }
    public string ShaderName { get; set; } = "default";

    public Cube(bool isEmpty = true, bool isSolid = false)
    {
        IsEmpty = isEmpty;
        IsSolid = isSolid;
    }
}