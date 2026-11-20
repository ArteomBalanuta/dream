using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// Never confines; records what it was asked. Used off Windows and in tests.
public sealed class NullCursorConfiner : ICursorConfiner
{
    public bool IsConfined => false;

    public int ConfineCalls { get; private set; }

    public int ReleaseCalls { get; private set; }

    public Box2i LastRect { get; private set; }

    public void Confine(Box2i clientRectScreen)
    {
        ConfineCalls++;
        LastRect = clientRectScreen;
    }

    public void Release() => ReleaseCalls++;
}
