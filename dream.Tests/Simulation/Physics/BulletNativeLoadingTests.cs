using System.Runtime.InteropServices;
using Xunit;

namespace Dream.Tests.Simulation.Physics;

// The Bullet natives must ship under the exact names the runtime probes for on each OS
// (libbulletc.dll / libbulletc.so), with none of BulletSharp's own per-OS copies alongside.
public class BulletNativeLoadingTests
{
    [Fact]
    public void LibBulletC_LoadsByDefaultProbing()
    {
        var assembly = typeof(BulletSharp.DiscreteDynamicsWorld).Assembly;

        Assert.True(NativeLibrary.TryLoad("libbulletc", assembly, null, out var handle));
        NativeLibrary.Free(handle);
    }

    [Fact]
    public void CurrentOsNativeFile_IsInOutputDirectory()
    {
        var name = OperatingSystem.IsWindows() ? "libbulletc.dll"
            : OperatingSystem.IsLinux() ? "libbulletc.so"
            : null;
        if (name == null)
        {
            return;
        }

        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), name);
    }

    [Theory]
    [InlineData("libbulletc-windows-x64.dll")]
    [InlineData("libbulletc-windows-x86.dll")]
    [InlineData("libbulletc-linux-x64.so")]
    [InlineData("libbulletc-linux-x86.so")]
    [InlineData("BulletSharp.dll.config")]
    public void PackageSideFiles_AreNotInOutputDirectory(string name)
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), name);
    }
}
