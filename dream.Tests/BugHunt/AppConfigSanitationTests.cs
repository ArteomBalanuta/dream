using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Dream.Configuration;
using Dream.Rendering.ViewModels;

namespace Dream.Tests.BugHunt;

/// <summary>
/// Regression tests for the BUGHUNTER-APP packet: config sanitation and the shipped config file.
/// </summary>
public class AppConfigSanitationTests
{
    // BH-APP-4: msaa=-2147483648 made MsaaSettings.Sanitize throw OverflowException from
    // Math.Abs(int.MinValue), inside the Game base-constructor argument list, before any window exists.
    [Fact]
    public void BH_APP_4_Sanitize_Handles_IntMinValue_Without_Throwing()
    {
        Assert.Equal(MsaaSettings.Off, MsaaSettings.Sanitize(int.MinValue));
        Assert.Equal(MsaaSettings.Off, MsaaSettings.Sanitize(int.MinValue + 1));
        Assert.Equal(4, MsaaSettings.Sanitize(int.MaxValue));
    }

    // BH-APP-4: the same value arriving through the text config and through DREAM_MSAA.
    [Fact]
    public void BH_APP_4_LoadOrCreate_Handles_IntMinValue_Msaa()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        File.WriteAllText(path, "msaa=-2147483648\n");

        DesktopClientConfig config = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal(MsaaSettings.Off, config.MsaaSamples);
        Assert.Equal(MsaaSettings.Off, MsaaSettings.Resolve(MsaaSettings.Default, "-2147483648"));
    }

    // BH-APP-4: a float that saturates to int.MinValue on the float branch of ApplyValue.
    [Fact]
    public void BH_APP_4_LoadOrCreate_Handles_Saturating_Float_Msaa()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        File.WriteAllText(path, "msaa=-3000000000\n");

        DesktopClientConfig config = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal(MsaaSettings.Off, config.MsaaSamples);
    }

    // BH-APP-4: valid counts are unchanged.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(-3, 0)]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(100, 4)]
    public void BH_APP_4_Sanitize_Keeps_The_Existing_Snapping(int value, int expected)
    {
        Assert.Equal(expected, MsaaSettings.Sanitize(value));
    }

    // BH-APP-3: Math.Clamp passes NaN straight through, so a NaN field of view survived Sanitize,
    // poisoned the projection matrix and was serialized back out as the literal "NaN".
    [Fact]
    public void BH_APP_3_NonFinite_Floats_Fall_Back_To_Defaults()
    {
        DesktopClientConfig config = new()
        {
            FieldOfViewDegrees = float.NaN,
            MouseSensitivity = float.PositiveInfinity,
            NoclipSpeed = float.NaN,
        };

        config.Sanitize();

        Assert.Equal(70f, config.FieldOfViewDegrees);
        Assert.Equal(2.5f, config.MouseSensitivity);
        Assert.Equal(18f, config.NoclipSpeed);
    }

    // BH-APP-3: through the file, and the saved file must not carry "NaN" back out.
    [Fact]
    public void BH_APP_3_Nan_In_File_Does_Not_Round_Trip()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "desktop-client.txt");
        File.WriteAllText(path, "field_of_view=NaN\nmouse_sensitivity=NaN\nnoclip_speed=NaN\n");

        DesktopClientConfig config = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.True(float.IsFinite(config.FieldOfViewDegrees));
        Assert.True(float.IsFinite(config.MouseSensitivity));
        Assert.True(float.IsFinite(config.NoclipSpeed));
        Assert.DoesNotContain("NaN", DesktopClientTextConfig.Serialize(config), StringComparison.Ordinal);
    }

    // BH-APP-3: valid in-range values are still left exactly as written.
    [Fact]
    public void BH_APP_3_Valid_Values_Are_Untouched()
    {
        DesktopClientConfig config = new()
        {
            FieldOfViewDegrees = 83f,
            MouseSensitivity = 1.25f,
            NoclipSpeed = 7.5f,
        };

        config.Sanitize();

        Assert.Equal(83f, config.FieldOfViewDegrees);
        Assert.Equal(1.25f, config.MouseSensitivity);
        Assert.Equal(7.5f, config.NoclipSpeed);
    }

    // BH-APP-6: viewmodel-rig.txt was never sanitized, so NaN/Infinity reached the held-item matrix
    // and the weapon or torch silently disappeared for the whole run.
    [Fact]
    public void BH_APP_6_Rig_Rejects_NonFinite_Values()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "viewmodel-rig.txt");
        ViewModelRigSettings defaults = ViewModelRigSettings.CreateDefault();
        File.WriteAllText(
            path,
            "body_anchor_drop=NaN\nheld_item_position_y=Infinity\nheld_item_rotation_x=-Infinity\n");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.Equal(defaults.BodyAnchor.VerticalDrop, settings.BodyAnchor.VerticalDrop);
        Assert.Equal(defaults.HeldItem.PositionOffset.Y, settings.HeldItem.PositionOffset.Y);
        Assert.Equal(defaults.HeldItem.RotationOffsetDegrees.X, settings.HeldItem.RotationOffsetDegrees.X);
    }

    // BH-APP-6: finite values still load exactly as written.
    [Fact]
    public void BH_APP_6_Rig_Keeps_Finite_Values()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "viewmodel-rig.txt");
        File.WriteAllText(path, "body_anchor_drop=0.42\nheld_item_position_y=-1.5\nheld_item_rotation_x=33\n");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.True(MathF.Abs(0.42f - settings.BodyAnchor.VerticalDrop) < 1e-4f);
        Assert.True(MathF.Abs(-1.5f - settings.HeldItem.PositionOffset.Y) < 1e-4f);
        Assert.True(MathF.Abs(33f - settings.HeldItem.RotationOffsetDegrees.X) < 1e-4f);
    }

    // BH-APP-2: the Options rows commit from inside the render pass, so a config save that cannot
    // write must report rather than throw out of OnRenderFrame.
    [Fact]
    public void BH_APP_2_TrySave_Reports_Instead_Of_Throwing_When_The_Path_Is_A_Directory()
    {
        // The target path is an existing directory, so the write cannot succeed.
        string directory = Directory.CreateTempSubdirectory().FullName;

        bool saved = DesktopClientTextConfig.TrySave(directory, new DesktopClientConfig());

        Assert.False(saved);
    }

    // BH-APP-2: a successful save still returns true and writes the file.
    [Fact]
    public void BH_APP_2_TrySave_Still_Writes_A_Normal_File()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "nested", "desktop-client.txt");

        Assert.True(DesktopClientTextConfig.TrySave(path, new DesktopClientConfig()));
        Assert.True(File.Exists(path));
    }

    // BH-APP-2: a config directory that cannot be seeded must still yield a usable default config
    // rather than throw out of the Game constructor.
    [Fact]
    public void BH_APP_2_LoadOrCreate_Falls_Back_To_Defaults_When_The_File_Cannot_Be_Written()
    {
        // Path points through a file, so both CreateDirectory and WriteAllText fail.
        string blocker = Path.Combine(Directory.CreateTempSubdirectory().FullName, "blocker");
        File.WriteAllText(blocker, "not a directory");
        string path = Path.Combine(blocker, "config", "desktop-client.txt");

        DesktopClientConfig config = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(70f, config.FieldOfViewDegrees);

        ViewModelRigSettings rig = ViewModelRigTextConfig.LoadOrCreate(
            Path.Combine(blocker, "config", "viewmodel-rig.txt"));
        Assert.Equal(ViewModelRigSettings.CreateDefault().BodyAnchor.VerticalDrop, rig.BodyAnchor.VerticalDrop);
    }

    // BH-APP-7: the shipped config file had drifted from the serializer — three keys no consumer reads
    // (strength, attack_speed, vitality) and eight the serializer emits but the file never mentioned.
    [Fact]
    public void BH_APP_7_Shipped_Config_Key_Set_Matches_The_Serializer()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, "config", "desktop-client.txt");
        Assert.True(File.Exists(shipped), $"shipped config missing at {shipped}");

        List<string> shippedKeys = KeysOf(File.ReadAllLines(shipped));
        List<string> serializedKeys = KeysOf(
            DesktopClientTextConfig.Serialize(new DesktopClientConfig())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(serializedKeys.OrderBy(k => k, StringComparer.Ordinal), shippedKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    private static List<string> KeysOf(IEnumerable<string> lines)
    {
        List<string> keys = [];
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                keys.Add(parts[0]);
            }
        }

        return keys;
    }
}
