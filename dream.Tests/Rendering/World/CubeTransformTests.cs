using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.World;
using BulletSharp.Math;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Rendering.World;


public class CubeTransformTests
{
    [Fact]
    public void CreateCenteredVisualModelMatrix_Keeps_Local_Origin_At_Cube_Center()
    {
        TkVector3 center = new TkVector3(3.5f, 1.5f, 7.5f);

        Matrix4 model = CubeTransforms.CreateCenteredVisualModelMatrix(center, 1f);

        Assert.Equal(center, model.ExtractTranslation());
    }

    [Fact]
    public void CreateCenteredVisualModelMatrix_WithAssetScale_UsesScaledUnitSize()
    {
        TkVector3 center = new TkVector3(3.5f, 1.5f, 7.5f);

        Matrix4 model = CubeTransforms.CreateCenteredVisualModelMatrix(center, 0.25f, 1.6f);

        Assert.Equal(center, model.ExtractTranslation());
        Assert.Equal(0.4f, model.M11, 3);
        Assert.Equal(0.4f, model.M22, 3);
        Assert.Equal(0.4f, model.M33, 3);
    }

    [Fact]
    public void CreateCollisionDebugModelMatrix_UsesCollisionHalfExtentsForDebugScale()
    {
        Matrix bulletTransform = Matrix.Translation(3.5f, 1.5f, 7.5f);

        Matrix4 model = CubeTransforms.CreateCollisionDebugModelMatrix(
            bulletTransform,
            new TkVector3(0.15f, 0.2f, 0.25f));

        Assert.Equal(new TkVector3(3.5f, 1.5f, 7.5f), model.ExtractTranslation());
        Assert.Equal(0.3f, model.M11, 3);
        Assert.Equal(0.4f, model.M22, 3);
        Assert.Equal(0.5f, model.M33, 3);
    }
}
