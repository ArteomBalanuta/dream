using System.IO;
using OpenTK.Mathematics;
using Xunit;

public class ViewModelRigTextConfigTests
{
    [Fact]
    public void LoadOrCreate_Writes_Default_File_When_Missing()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "viewmodel-rig.txt");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.True(File.Exists(path));
        Assert.Equal(0.306f, settings.BodyAnchor.VerticalDrop);
        Assert.Equal(new Vector3(-0.046f, 0.204f, -0.156f), settings.HeldItem.PositionOffset);
    }

    [Fact]
    public void LoadOrCreate_Overrides_Recognized_Values_From_File()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "viewmodel-rig.txt");
        File.WriteAllText(path, "body_anchor_drop=0.500\nheld_item_position_x=0.125\n");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.Equal(0.500f, settings.BodyAnchor.VerticalDrop);
        Assert.Equal(0.125f, settings.HeldItem.PositionOffset.X);
        Assert.Equal(0.204f, settings.HeldItem.PositionOffset.Y);
    }

    [Fact]
    public void LoadOrCreate_Falls_Back_Per_Field_On_Invalid_Value()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "viewmodel-rig.txt");
        File.WriteAllText(path, "body_anchor_drop=bad\nheld_item_rotation_y=1.500\n");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.Equal(0.306f, settings.BodyAnchor.VerticalDrop);
        Assert.Equal(1.500f, settings.HeldItem.RotationOffsetDegrees.Y);
    }

    [Fact]
    public void LoadOrCreate_Ignores_Unknown_Keys_And_Comments()
    {
        string tempDir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(tempDir, "viewmodel-rig.txt");
        File.WriteAllText(path, "# comment\nunknown_key=123\n\nbody_anchor_spread=0.100\n");

        ViewModelRigSettings settings = ViewModelRigTextConfig.LoadOrCreate(path);

        Assert.Equal(0.100f, settings.BodyAnchor.HorizontalSpread);
        Assert.Equal(0.306f, settings.BodyAnchor.VerticalDrop);
    }
}
