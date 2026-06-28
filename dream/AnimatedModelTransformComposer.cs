using OpenTK.Mathematics;

public static class AnimatedModelTransformComposer
{
    public static Matrix4 ComposeInstanceNodeMatrix(Matrix4 instanceModelMatrix, Matrix4 nodeWorldMatrix)
    {
        return nodeWorldMatrix * instanceModelMatrix;
    }

    public static Matrix4 ComposeSkinJointMatrix(Matrix4 jointWorldMatrix, Matrix4 inverseBindMatrix)
    {
        return jointWorldMatrix * inverseBindMatrix;
    }
}
