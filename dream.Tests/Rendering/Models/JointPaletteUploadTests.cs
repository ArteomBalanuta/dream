using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Models;
using Xunit;

namespace Dream.Tests.Rendering.Models;

// A node's joint palette goes to each program object (base and MASK variant) at most once per
// node call, however many primitives it has. GL is not available, so the record is tested alone.
public class JointPaletteUploadTests
{
    private static void Upload(ref AnimatedModel.JointPaletteUploads record, object program, ref int uploads)
    {
        if (record.TryMark(program))
        {
            uploads++;
        }
    }

    [Fact]
    public void Many_Primitives_Across_Two_Programs_Upload_Exactly_Twice()
    {
        object baseProgram = new();
        object maskProgram = new();
        AnimatedModel.JointPaletteUploads record = default;
        int uploads = 0;
        for (int i = 0; i < 9; i++)
        {
            Upload(ref record, i % 3 == 2 ? maskProgram : baseProgram, ref uploads);
        }

        Assert.Equal(2, uploads);
    }

    [Fact]
    public void Each_Node_Call_Starts_A_Fresh_Record()
    {
        object baseProgram = new();
        object maskProgram = new();
        int uploads = 0;
        for (int node = 0; node < 2; node++)
        {
            AnimatedModel.JointPaletteUploads record = default;
            for (int i = 0; i < 5; i++)
            {
                Upload(ref record, i % 2 == 0 ? baseProgram : maskProgram, ref uploads);
            }
        }

        Assert.Equal(4, uploads);
    }

    // Synthetic negative: the same counting path with a fresh record per primitive (the old
    // per-primitive behaviour) uploads once per primitive, so the guarded count can fail.
    [Fact]
    public void A_Fresh_Record_Per_Primitive_Uploads_Once_Per_Primitive()
    {
        object baseProgram = new();
        object maskProgram = new();
        int unguarded = 0;
        int guarded = 0;
        AnimatedModel.JointPaletteUploads shared = default;
        for (int i = 0; i < 9; i++)
        {
            object program = i % 3 == 2 ? maskProgram : baseProgram;
            AnimatedModel.JointPaletteUploads fresh = default;
            Upload(ref fresh, program, ref unguarded);
            Upload(ref shared, program, ref guarded);
        }

        Assert.Equal(9, unguarded);
        Assert.Equal(2, guarded);
    }

    [Fact]
    public void ApplySkinning_Guards_The_Upload_With_The_Per_Program_Record_And_Still_Writes_UseSkinning()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Models", "AnimatedModel.cs"));
        int start = source.IndexOf("private void ApplySkinning(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string body = source[start..source.IndexOf("internal struct JointPaletteUploads", start, StringComparison.Ordinal)];

        Assert.Matches(new Regex(@"if \(uploaded\.TryMark\(shader\)\)\s*\{\s*UploadJointMatrices\(shader\.GetUniformLocations\(JointMatrixArrayUniformName\)\[0\], jointMatrices\);"), body);
        Assert.Single(Regex.Matches(body, @"UploadJointMatrices\("));
        Assert.Contains("shader.SetBool(\"useSkinning\", false);", body);
        Assert.Contains("shader.SetBool(\"useSkinning\", true);", body);

        // The record is fresh per node call.
        Assert.Equal(2, Regex.Matches(source, @"JointPaletteUploads uploaded = default;").Count);
        Assert.Equal(2, Regex.Matches(source, @"ref jointsComposed, ref uploaded\)").Count);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "CLAUDE.md")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}
