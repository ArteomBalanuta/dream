using OpenTK.Mathematics;

namespace Dream.Rendering.Core;

public sealed class ReusableMatrixBuffer
{
    private Matrix4[] _items = [];

    public int Capacity => _items.Length;

    public Matrix4[] Rent(int count)
    {
        if (count <= _items.Length)
        {
            return _items;
        }

        int capacity = Math.Max(count, Math.Max(16, _items.Length * 2));
        _items = new Matrix4[capacity];
        return _items;
    }
}
