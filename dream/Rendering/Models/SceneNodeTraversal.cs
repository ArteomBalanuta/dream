using SharpGLTF.Schema2;
using Dream.Simulation.World;

namespace Dream.Rendering.Models;


public static class SceneNodeTraversal
{
    public static IEnumerable<Node> EnumerateRenderableNodes(IEnumerable<Node> roots)
    {
        foreach (Node root in roots)
        {
            foreach (Node node in EnumerateNodeAndDescendants(root))
            {
                if (node.Mesh != null)
                {
                    yield return node;
                }
            }
        }
    }

    // Same depth-first pre-order as EnumerateRenderableNodes, so draw order (and with it
    // transparency) does not change. Meant for load time: callers keep the result and loop over it.
    public static void CollectRenderableNodes(IEnumerable<Node> roots, List<Node> results)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(results);

        foreach (Node root in roots)
        {
            CollectRenderableNodeAndDescendants(root, results);
        }
    }

    private static void CollectRenderableNodeAndDescendants(Node node, List<Node> results)
    {
        if (node.Mesh != null)
        {
            results.Add(node);
        }

        foreach (Node child in node.VisualChildren)
        {
            CollectRenderableNodeAndDescendants(child, results);
        }
    }

    private static IEnumerable<Node> EnumerateNodeAndDescendants(Node node)
    {
        yield return node;

        foreach (Node child in node.VisualChildren)
        {
            foreach (Node descendant in EnumerateNodeAndDescendants(child))
            {
                yield return descendant;
            }
        }
    }
}
