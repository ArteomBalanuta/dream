namespace Dream.Client.Navigation;

public interface INavWorld
{
    int Width { get; }

    int Height { get; }

    int Depth { get; }

    bool IsBlocked(int x, int y, int z);

    bool IsSupport(int x, int y, int z);
}
