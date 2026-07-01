using System.Linq;
using System.Reflection;
using Xunit;
using Dream.Rendering.Models;

namespace Dream.Tests.Rendering.Models;


public class AnimatedModelRenderSignatureTests
{
    [Fact]
    public void BeginRender_Exposes_Optional_Culling_Flag()
    {
        MethodInfo method = typeof(AnimatedModel)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(m => m.Name == "BeginRender" && m.GetParameters().Length == 4);

        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(typeof(bool), parameters[3].ParameterType);
        Assert.True(parameters[3].HasDefaultValue);
        Assert.Equal(true, parameters[3].DefaultValue);
    }
}
