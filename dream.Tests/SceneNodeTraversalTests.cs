using System.Linq;
using SharpGLTF.Schema2;
using Xunit;

public class SceneNodeTraversalTests
{
    [Fact]
    public void EnumerateRenderableNodes_Finds_Mesh_Nodes_Below_Scene_Root()
    {
        ModelRoot model = ModelRoot.Load(WolfAssetDefinition.ModelPath);

        string[] names = SceneNodeTraversal
            .EnumerateRenderableNodes(model.DefaultScene.VisualChildren)
            .Select(node => node.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("Cube", names);
    }
}
