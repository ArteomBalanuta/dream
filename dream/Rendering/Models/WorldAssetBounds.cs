using OpenTK.Mathematics;
using SharpGLTF.Schema2;

namespace Dream.Rendering.Models;

public static class WorldAssetBounds
{
    public static WorldAssetDimensions MeasureDimensions(string modelPath)
    {
        ModelRoot model = ModelRoot.Load(modelPath);
        bool hasAnyPosition = false;
        Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        foreach (Node node in SceneNodeTraversal.EnumerateRenderableNodes(model.DefaultScene.VisualChildren))
        {
            Matrix4 nodeWorld = node.WorldMatrix.ToOpenTK();
            foreach (MeshPrimitive primitive in node.Mesh!.Primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
                foreach (System.Numerics.Vector3 position in positions)
                {
                    Vector3 transformed = Vector3.TransformPosition(
                        new Vector3(position.X, position.Y, position.Z),
                        nodeWorld);

                    min = Vector3.ComponentMin(min, transformed);
                    max = Vector3.ComponentMax(max, transformed);
                    hasAnyPosition = true;
                }
            }
        }

        if (!hasAnyPosition)
        {
            return default;
        }

        Vector3 size = max - min;
        return new WorldAssetDimensions(
            Height: size.Y,
            Width: size.X,
            Depth: size.Z);
    }
}
