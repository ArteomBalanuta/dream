using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Dream.Simulation.Physics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.Physics;

// Wave 9 tester, BulletProfilerGuard (the libbulletc btGetCurrentThreadIndex patch): idempotent
// across many integrators, safe when many threads construct integrators at once, and a library
// whose code does not match is left alone and reported (never patched twice, never a crash).
// The test project is not compiled with unsafe code, so the guard's pointer-taking helpers are
// called through small dynamic methods (native int and pointers are the same IL type).
public class Wave9BulletProfilerGuardTests
{
    private static readonly bool IsWindowsX64 =
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly ITestOutputHelper _output;

    public Wave9BulletProfilerGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ManyIntegrators_FromManyThreadsAtOnce_LeaveOnePatchInPlace()
    {
        using (new PhysicsIntegrator())
        {
        }

        BulletProfilerGuardStatus status = BulletProfilerGuard.Status;
        string detail = BulletProfilerGuard.Detail;
        _output.WriteLine($"guard: {status} ({detail})");
        byte[] before = IsWindowsX64 && status == BulletProfilerGuardStatus.Patched ? ReadPatchedBytes() : [];

        const int Threads = 16;
        const int PerThread = 12;
        using Barrier start = new(Threads);
        int[] indices = new int[Threads];
        Exception?[] failures = new Exception?[Threads];
        Thread[] threads = new Thread[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int slot = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (int i = 0; i < PerThread; i++)
                    {
                        BulletProfilerGuard.EnsureApplied();
                        using PhysicsIntegrator physics = new();
                        physics.StepSimulationFixed(1f / 64f);
                    }

                    indices[slot] = status == BulletProfilerGuardStatus.Patched
                        ? BulletProfilerGuard.ReadCurrentThreadIndexForTest()
                        : BulletProfilerGuard.DisabledThreadIndex;
                }
                catch (Exception ex)
                {
                    failures[slot] = ex;
                }
            });
            threads[t].Start();
        }

        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        }

        Assert.All(failures, failure => Assert.Null(failure));
        Assert.Equal(status, BulletProfilerGuard.Status);
        Assert.Equal(detail, BulletProfilerGuard.Detail);
        if (!IsWindowsX64 || status != BulletProfilerGuardStatus.Patched)
        {
            return;
        }

        Assert.All(indices, index => Assert.Equal(BulletProfilerGuard.DisabledThreadIndex, index));
        Assert.Equal(before, ReadPatchedBytes());
        Assert.Equal(PatchBytes(), ReadPatchedBytes());
    }

    // The patched function no longer matches the signature, so a second Apply (were the status
    // ever reset) would find no match and report Unsupported instead of writing again.
    [Fact]
    public void PatchedCode_NoLongerMatchesTheSignature()
    {
        using (new PhysicsIntegrator())
        {
        }

        if (!IsWindowsX64 || BulletProfilerGuard.Status != BulletProfilerGuardStatus.Patched)
        {
            _output.WriteLine($"guard {BulletProfilerGuard.Status}: nothing to check");
            return;
        }

        Assert.False(Matches(FunctionAddress()));
    }

    // The matcher on synthetic code: the signature with its wildcards filled in matches; any
    // fixed byte changed (a different libbulletc build) does not.
    [Fact]
    public void Signature_MatchesItsOwnBytes_AndRejectsAnyChangedFixedByte()
    {
        short[] signature = Signature();
        byte[] code = signature.Select(value => value < 0 ? (byte)0x5A : (byte)value).ToArray();
        IntPtr buffer = Marshal.AllocHGlobal(code.Length);
        try
        {
            Marshal.Copy(code, 0, buffer, code.Length);
            Assert.True(Matches(buffer));

            int checkedBytes = 0;
            for (int i = 0; i < signature.Length; i++)
            {
                if (signature[i] < 0)
                {
                    continue;
                }

                byte original = code[i];
                Marshal.WriteByte(buffer, i, (byte)(original ^ 0x01));
                Assert.False(Matches(buffer), $"byte {i} changed still matched");
                Marshal.WriteByte(buffer, i, original);
                checkedBytes++;
            }

            Assert.True(checkedBytes > 60);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // A module that is not a PE image, or a PE image without a .text section, is reported as not
    // found (Unsupported) instead of being read past its end.
    [Fact]
    public void TextSectionLookup_RejectsNonImages_AndImagesWithoutText()
    {
        IntPtr buffer = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.Copy(new byte[4096], 0, buffer, 4096);
            Assert.False(TryFindTextSection(buffer, out _, out _));

            // A minimal PE header: "MZ", e_lfanew = 0x40, "PE\0\0", one section named ".data".
            Marshal.WriteByte(buffer, 0, (byte)'M');
            Marshal.WriteByte(buffer, 1, (byte)'Z');
            Marshal.WriteInt32(buffer, 0x3C, 0x40);
            Marshal.WriteInt32(buffer, 0x40, 0x00004550);
            Marshal.WriteInt16(buffer, 0x40 + 6, 1);
            Marshal.WriteInt16(buffer, 0x40 + 20, 0);
            IntPtr section = buffer + 0x40 + 24;
            byte[] name = ".data\0\0\0"u8.ToArray();
            Marshal.Copy(name, 0, section, name.Length);
            Marshal.WriteInt32(section, 8, 16);
            Marshal.WriteInt32(section, 12, 0x200);
            Assert.False(TryFindTextSection(buffer, out _, out _));

            // Renamed to ".text", it is found where its header says.
            byte[] text = ".text\0\0\0"u8.ToArray();
            Marshal.Copy(text, 0, section, text.Length);
            Assert.True(TryFindTextSection(buffer, out IntPtr found, out int length));
            Assert.Equal(buffer + 0x200, found);
            Assert.Equal(16, length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static short[] Signature() =>
        (short[])typeof(BulletProfilerGuard).GetField("s_signature", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static byte[] PatchBytes() =>
        (byte[])typeof(BulletProfilerGuard).GetField("s_patch", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static IntPtr FunctionAddress() =>
        (IntPtr)typeof(BulletProfilerGuard).GetField("s_function", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static byte[] ReadPatchedBytes()
    {
        byte[] bytes = new byte[PatchBytes().Length];
        Marshal.Copy(FunctionAddress(), bytes, 0, bytes.Length);
        return bytes;
    }

    private static readonly Func<IntPtr, bool> s_matches = CreateMatches();
    private static readonly TryFindTextSectionDelegate s_tryFindTextSection = CreateTryFindTextSection();

    private delegate bool TryFindTextSectionDelegate(IntPtr module, IntPtr textOut, IntPtr lengthOut);

    private static bool Matches(IntPtr code) => s_matches(code);

    private static bool TryFindTextSection(IntPtr module, out IntPtr text, out int length)
    {
        IntPtr outputs = Marshal.AllocHGlobal(IntPtr.Size + sizeof(int));
        try
        {
            bool found = s_tryFindTextSection(module, outputs, outputs + IntPtr.Size);
            text = Marshal.ReadIntPtr(outputs);
            length = Marshal.ReadInt32(outputs + IntPtr.Size);
            return found;
        }
        finally
        {
            Marshal.FreeHGlobal(outputs);
        }
    }

    private static Func<IntPtr, bool> CreateMatches()
    {
        MethodInfo target = typeof(BulletProfilerGuard).GetMethod("Matches", BindingFlags.Static | BindingFlags.NonPublic)!;
        DynamicMethod method = new("Matches", typeof(bool), [typeof(IntPtr)], typeof(BulletProfilerGuard).Module, skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<IntPtr, bool>>();
    }

    private static TryFindTextSectionDelegate CreateTryFindTextSection()
    {
        MethodInfo target = typeof(BulletProfilerGuard).GetMethod("TryFindTextSection", BindingFlags.Static | BindingFlags.NonPublic)!;
        DynamicMethod method = new(
            "TryFindTextSection",
            typeof(bool),
            [typeof(IntPtr), typeof(IntPtr), typeof(IntPtr)],
            typeof(BulletProfilerGuard).Module,
            skipVisibility: true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<TryFindTextSectionDelegate>();
    }
}
