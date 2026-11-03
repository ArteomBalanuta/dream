using Xunit;
using Dream.Server;

namespace Dream.Tests.Server;

// Process-wide environment variable, so this runs alone.
[Collection(TimingSensitiveCollection.Name)]
public sealed class ServerBootstrapDevCommandsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("TRUE", false)]
    [InlineData("yes", false)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    [InlineData("11", false)]
    [InlineData("1", true)]
    public void CreateRuntimeHost_AllowsDevCommandsOnlyForExactlyOne(string? value, bool expected)
    {
        string? previous = Environment.GetEnvironmentVariable(ServerBootstrap.DevCommandsEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.DevCommandsEnvironmentVariable, value);

            using var host = ServerBootstrap.CreateRuntimeHost(persistence: null, log: null);

            Assert.Equal(expected, host.DevCommandsAllowed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServerBootstrap.DevCommandsEnvironmentVariable, previous);
        }
    }
}
