using System.Collections.Concurrent;
using System.Numerics;
using SharpGLTF.Schema2;
using Xunit;

namespace Dream.Tests.Rendering.Models;

// Parsed glTF models shared read-only by the tests of one class (one fixture per class, so an
// instance is never used by two threads at once). A test asks for a model by path and by role,
// and every role gets its own ModelRoot, so a test that compares two independent loads (a
// SharpGLTF reference against one AnimatedModelPose reads) still gets two.
//
// Only tests that neither mutate nor pose the glTF nodes may use a shared instance; a test that
// writes node transforms (for example ApplyToGltfNodes) loads its own. Dispose checks that every
// node's local transform and every logical-list count is unchanged since the load, so a test
// that wrote into a shared model fails its class instead of silently skewing the others.
public sealed class GltfModelRootFixture : IDisposable
{
    private readonly ConcurrentDictionary<(string Path, string Role), Lazy<Loaded>> _models = new();

    public ModelRoot Get(string modelPath, string role)
    {
        return _models.GetOrAdd((modelPath, role), key => new Lazy<Loaded>(() => Loaded.From(key.Path))).Value.Model;
    }

    public void Dispose()
    {
        foreach (((string path, string role), Lazy<Loaded> lazy) in _models)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.AssertUnchanged($"{Path.GetFileName(path)} ({role})");
            }
        }
    }

    private sealed record Loaded(ModelRoot Model, Matrix4x4[] LocalMatrices, int Nodes, int Animations, int Meshes, int Skins)
    {
        public static Loaded From(string modelPath)
        {
            ModelRoot model = ModelRoot.Load(modelPath);
            return new Loaded(
                model,
                model.LogicalNodes.Select(node => node.LocalMatrix).ToArray(),
                model.LogicalNodes.Count,
                model.LogicalAnimations.Count,
                model.LogicalMeshes.Count,
                model.LogicalSkins.Count);
        }

        public void AssertUnchanged(string what)
        {
            Assert.Equal(Nodes, Model.LogicalNodes.Count);
            Assert.Equal(Animations, Model.LogicalAnimations.Count);
            Assert.Equal(Meshes, Model.LogicalMeshes.Count);
            Assert.Equal(Skins, Model.LogicalSkins.Count);
            for (int i = 0; i < LocalMatrices.Length; i++)
            {
                Assert.True(
                    LocalMatrices[i] == Model.LogicalNodes[i].LocalMatrix,
                    $"{what}: node {i}'s local transform changed while the model was shared.");
            }
        }
    }
}
