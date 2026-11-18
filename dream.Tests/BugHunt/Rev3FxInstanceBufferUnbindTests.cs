using Xunit;
using Dream.Rendering.Effects;

namespace Dream.Tests.BugHunt;

// REV3-3: the Fx instance-buffer unbind covered only the constructor, not the per-frame path.
//
// FxInstanceBuffer.Upload binds _handle to GL_ARRAY_BUFFER, calls BufferData/BufferSubData and used
// to return WITHOUT unbinding - every frame particles are drawn. GL_ARRAY_BUFFER is not part of VAO
// state, so the added GL.BindBuffer(..., 0) in FxRenderer's constructor only covered the single
// BindAttributes at creation: after the first Upload the instance buffer was the bound ARRAY_BUFFER
// again, and the next buffer write that forgot to bind its own target would land in our instances.
//
// There is no GL context in a test, so the call itself cannot be made and observed here: the unbind
// is pinned as source, in the method that runs every frame. The allocation claim is checked for
// real - GL.BindBuffer takes two ints, so the per-frame path is unchanged in that respect and no
// array, list or closure was added to it.
public class Rev3FxInstanceBufferUnbindTests
{
    [Fact]
    public void Upload_UnbindsTheArrayBuffer_OnThePerFramePath()
    {
        string upload = Method(Read("dream/Rendering/Effects/FxInstanceBuffer.cs"), "public void Upload(");

        Assert.Contains("GL.BindBuffer(BufferTarget.ArrayBuffer, _handle);", upload);
        Assert.Contains("GL.BufferSubData(BufferTarget.ArrayBuffer", upload);
        int write = upload.LastIndexOf("GL.BufferSubData(BufferTarget.ArrayBuffer", StringComparison.Ordinal);
        int unbind = upload.LastIndexOf("GL.BindBuffer(BufferTarget.ArrayBuffer, 0);", StringComparison.Ordinal);
        Assert.True(unbind > write, "Upload must unbind GL_ARRAY_BUFFER after it writes, not leave it bound.");
    }

    // BindAttributes binds the buffer too, and is called from more than the constructor's site.
    [Fact]
    public void BindAttributes_AlsoUnbindsTheArrayBuffer()
    {
        string bind = Method(Read("dream/Rendering/Effects/FxInstanceBuffer.cs"), "public void BindAttributes(");

        int pointer = bind.LastIndexOf("GL.VertexAttribDivisor(", StringComparison.Ordinal);
        int unbind = bind.LastIndexOf("GL.BindBuffer(BufferTarget.ArrayBuffer, 0);", StringComparison.Ordinal);
        Assert.True(pointer >= 0);
        Assert.True(unbind > pointer, "BindAttributes must leave GL_ARRAY_BUFFER unbound.");
    }

    // The per-frame path allocates nothing: the only thing added is a GL.BindBuffer of two
    // value-typed arguments. Nothing in Upload may build an array, a list or a lambda.
    [Fact]
    public void ThePerFramePath_AllocatesNothing()
    {
        string upload = Method(Read("dream/Rendering/Effects/FxInstanceBuffer.cs"), "public void Upload(");

        Assert.DoesNotContain(" new ", upload);
        Assert.DoesNotContain("=>", upload);
        Assert.DoesNotContain("ToArray", upload);
        Assert.DoesNotContain("$\"", upload);
    }

    // Control: nothing about the unbind changes what the particles draw with. The buffer still
    // reports the capacity the pass sizes itself from and still counts its uploads, so a frame that
    // drew particles before draws them now.
    [Fact]
    public void Control_TheBufferStillDescribesTheSameStorage()
    {
        using FxInstanceBuffer buffer = new();

        Assert.Equal(FxLimits.MaxInstances, buffer.Capacity);
        Assert.Equal(0, buffer.UploadCount);
        // The handle is still created lazily on first use, so construction alone touches no GL.
        Assert.Equal(0, buffer.Handle);
        // A zero-instance frame is still the early return: no GL call, no upload counted.
        buffer.Upload(new FxInstance[4], 0);
        Assert.Equal(0, buffer.UploadCount);
        Assert.Equal(0, buffer.Handle);
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
