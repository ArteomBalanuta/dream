using SharpGLTF.Schema2;

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
