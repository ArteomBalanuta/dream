using OpenTK.Mathematics;

public static class AnimatedModelTransformComposer
{
    public static Matrix4 ComposeInstanceNodeMatrix(Matrix4 instanceModelMatrix, Matrix4 nodeWorldMatrix)
    {
        return nodeWorldMatrix * instanceModelMatrix;
    }

    public static Matrix4 ComposeSkinJointMatrix(
        Matrix4 meshWorldMatrix,
        Matrix4 jointWorldMatrix,
        Matrix4 inverseBindMatrix)
    {
        Matrix4 inverseMeshWorldMatrix = meshWorldMatrix;
        inverseMeshWorldMatrix.Invert();
        return inverseBindMatrix * jointWorldMatrix * inverseMeshWorldMatrix;
    }
}
