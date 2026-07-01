using System.IO;
using OpenTK.Mathematics;
using Xunit;

public class DesktopClientTextConfigTests
{
    [Fact]
    public void LoadOrCreate_Writes_Default_File_When_Missing()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "desktop-client.txt");

        DesktopClientConfig settings = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal(ServerBootstrap.DefaultPort, settings.Port);
        Assert.Equal("dream-client", settings.PlayerName);
        Assert.Equal(70f, settings.FieldOfViewDegrees);
        Assert.Equal(2.5f, settings.MouseSensitivity);
        Assert.Equal(18f, settings.NoclipSpeed);
        Assert.Equal(CameraViewMode.FirstPerson, settings.CameraViewMode);
        Assert.Equal(10, settings.Strength);
        Assert.Equal(2f, settings.AttackSpeed);
        Assert.Equal(10, settings.Vitality);
        Assert.Equal(FloatingCombatTextTuning.Default, settings.FloatingCombatText);
    }

    [Fact]
    public void LoadOrCreate_Overrides_Recognized_Values_And_Clamps_Invalid_Ranges()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "desktop-client.txt");
        File.WriteAllText(path, string.Join('\n', new[]
        {
            "host= 10.0.0.4 ",
            "port=70000",
            "player_name= ranger ",
            "field_of_view=82",
            "mouse_sensitivity=3.25",
            "camera_view=third_person",
            "noclip_speed=44",
            "strength=99",
            "attack_speed=0.1",
            "vitality=12",
            "damage_text_size=2.75",
            "damage_text_speed=0.55",
            "damage_text_crit_depth=5.5"
        }));

        DesktopClientConfig settings = DesktopClientTextConfig.LoadOrCreate(path);

        Assert.Equal("10.0.0.4", settings.Host);
        Assert.Equal(ServerBootstrap.DefaultPort, settings.Port);
        Assert.Equal("ranger", settings.PlayerName);
        Assert.Equal(82f, settings.FieldOfViewDegrees);
        Assert.Equal(3.25f, settings.MouseSensitivity);
        Assert.Equal(CameraViewMode.ThirdPerson, settings.CameraViewMode);
        Assert.Equal(44f, settings.NoclipSpeed);
        Assert.Equal(PlayerCombatStats.MaxStrength, settings.Strength);
        Assert.Equal(PlayerCombatStats.MinAttackSpeed, settings.AttackSpeed);
        Assert.Equal(12, settings.Vitality);
        Assert.Equal(2.75f, settings.FloatingCombatText.SizeMultiplier);
        Assert.Equal(0.55f, settings.FloatingCombatText.MotionSpeedMultiplier);
        Assert.Equal(5.5f, settings.FloatingCombatText.CriticalDepthMultiplier);
    }

    [Fact]
    public void ApplyToGameSettings_Seeds_Runtime_Menu_Values()
    {
        DesktopClientConfig config = new()
        {
            FieldOfViewDegrees = 88f,
            MouseSensitivity = 4.25f,
            CameraViewMode = CameraViewMode.ThirdPerson,
            NoclipSpeed = 33f,
            FloatingCombatText = new FloatingCombatTextTuning(2.4f, 0.65f, 4.75f)
        };
        GameSettings settings = new(new Vector2i(1440, 1024), ViewModelRigSettings.CreateDefault(), config);

        Assert.Equal(88f, settings.FieldOfViewDegrees);
        Assert.Equal(4.25f, settings.MouseSensitivity);
        Assert.Equal(CameraViewMode.ThirdPerson, settings.CameraViewMode);
        Assert.Equal(33f, settings.NoclipSpeed);
        Assert.Equal(new FloatingCombatTextTuning(2.4f, 0.65f, 4.75f), settings.FloatingCombatText);
    }
}
