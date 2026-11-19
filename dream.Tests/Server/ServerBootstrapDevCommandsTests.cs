using Xunit;
using Dream.Server;

namespace Dream.Tests.Server;

// Process-wide environment variable, so this runs alone.
[Collection(TimingSensitiveCollection.Name)]
public sealed class ServerBootstrapDevCommandsTests
{
    [Theory]
    [InlineData("0.0.0.0", true, true)]
    [InlineData("::", true, true)]
    [InlineData("192.168.1.5", true, true)]
    [InlineData("127.0.0.1", true, false)]
    [InlineData("::1", true, false)]
    [InlineData("0.0.0.0", false, false)]
    public void DevCommandsExposureWarning_FiresOnlyForDevCommandsOnANonLoopbackBind(string bind, bool devOn, bool expectWarning)
    {
        string? warning = ServerBootstrap.DevCommandsExposureWarning(devOn, System.Net.IPAddress.Parse(bind));

        Assert.Equal(expectWarning, warning != null);
        if (warning != null)
        {
            Assert.StartsWith("WARNING", warning, StringComparison.Ordinal);
            Assert.Contains("tunnel", warning, StringComparison.Ordinal);
        }
    }

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
