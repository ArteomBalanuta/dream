using System.Reflection;
using Xunit;
using Dream.Rendering.Actors;

namespace Dream.Tests.Rendering.Actors;


public class EnemyRendererSignatureTests
{
    [Fact]
    public void Draw_Accepts_Debug_Collision_Flag()
    {
        MethodInfo method = typeof(EnemyRenderer).GetMethod("Draw")!;

        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(bool), parameters[3].ParameterType);
    }
}
