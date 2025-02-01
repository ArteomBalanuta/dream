public class Cube
{
    public int Side { get; } = 1; // Use properties and make Side immutable
    public bool IsEmpty { get; set; } = true;
    public bool IsSolid { get; set; } = false;

    // Optional: Add a constructor for initialization
    public Cube(bool isEmpty = true, bool isSolid = false)
    {
        IsEmpty = isEmpty;
        IsSolid = isSolid;
    }
}