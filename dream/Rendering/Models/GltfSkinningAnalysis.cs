using System.Linq;
using SharpGLTF.Schema2;

namespace Dream.Rendering.Models;


public readonly record struct GltfSkinningAnalysisResult(
    bool HasSkins,
    bool HasSkinnedPrimitives,
    int JointCount);

public static class GltfSkinningAnalysis
{
    public static GltfSkinningAnalysisResult Analyze(string filePath)
    {
        ModelRoot model = ModelRoot.Load(filePath);
        bool hasSkins = model.LogicalSkins.Count > 0;
        bool hasSkinnedPrimitives = model.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Any(primitive =>
                primitive.GetVertexAccessor("JOINTS_0") != null &&
                primitive.GetVertexAccessor("WEIGHTS_0") != null);
        int jointCount = hasSkins ? model.LogicalSkins[0].JointsCount : 0;

        return new GltfSkinningAnalysisResult(
            hasSkins,
            hasSkinnedPrimitives,
            jointCount);
    }
}
