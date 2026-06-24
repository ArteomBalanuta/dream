using OpenTK.Mathematics;
using Xunit;

public class CubeTransformTests
{
    [Fact]
    public void CreateCenteredVisualModelMatrix_Keeps_Local_Origin_At_Cube_Center()
    {
        Vector3 center = new Vector3(3.5f, 1.5f, 7.5f);

        Matrix4 model = CubeTransforms.CreateCenteredVisualModelMatrix(center, 1f);

        Assert.Equal(center, model.ExtractTranslation());
    }
}
