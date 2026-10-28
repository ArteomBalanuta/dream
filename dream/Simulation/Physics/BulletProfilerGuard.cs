using System.Runtime.InteropServices;

namespace Dream.Simulation.Physics;

public enum BulletProfilerGuardStatus
{
    NotRun,
    Patched,
    // Turned off with DREAM_BULLET_PROFILER_GUARD=0.
    Disabled,
    // Not Windows x64, libbulletc not found, or its code is not the build this guard knows.
    Unsupported,
    Failed
}

// Works around a thread-safety bug in the native libbulletc that BulletSharp 0.11.1 bundles.
//
// That build has Bullet's built-in profiler (quickprof, BT_PROFILE) compiled in. Every profiled
// Bullet call (stepSimulation, removeRigidBody, the pair cache, ...) starts and stops a zone in a
// profile tree chosen by btGetCurrentThreadIndex(). That index lives in a thread-local, comes from
// a process-wide counter and wraps: after 63 threads the next thread gets index 1 again. A process
// in which more than 63 threads ever call into Bullet (thread-pool connection loops releasing
// seats, parallel tests, a long-running server) therefore ends up with two live threads sharing
// one index. Their interleaved Start_Profile/Stop_Profile calls walk the shared tree past its root
// to null, and the next Start_Profile on that index reads through the null pointer: 0xC0000005,
// which no managed code can catch. The worlds involved can be completely unrelated; the wave-9
// crash dump had DedicatedServerHost.Dispose and a client-side test, both on index 34, faulting at
// the same instruction.
//
// The library exports no way to turn the profiler off, and the thread index serves nothing else we
// use (only the profiler and Bullet's multithreaded dispatcher/solver pools, which dream does not
// create). So the guard patches btGetCurrentThreadIndex itself to return BT_MAX_THREAD_COUNT (64):
// quickprof treats an index >= 64 as "no profiling" and returns at once, and the Mt pools treat it
// as "no per-thread slot" and use their fallback. The function is found by its exact byte pattern
// (relative displacements masked) and patched only when there is exactly one match, so a different
// libbulletc build is left alone and reported as Unsupported.
//
// The patch covers every caller of the function. The same thread-index logic is also inlined once
// more, in btSetTaskScheduler (libbulletc+0x111990 in this build); that copy is left unpatched.
// It is harmless: dream never installs a task scheduler, and that copy only assigns an index to
// the calling thread, which quickprof never reads (quickprof calls the patched function).
//
// It runs once, from the first PhysicsIntegrator constructor, before that integrator creates its
// world. Production code creates every Bullet world through PhysicsIntegrator. Tests also create
// raw BulletSharp worlds, which may already be stepping on other threads when the guard runs, so
// a thread can be about to execute the function's first bytes while they are rewritten. The six
// patch bytes are therefore written together with the two original bytes after them as a single
// aligned 8-byte interlocked store: another thread decodes either the whole original prologue or
// the whole patch, never a mix.
public static class BulletProfilerGuard
{
    // BT_MAX_THREAD_COUNT: every quickprof entry point returns early for an index >= this.
    public const int DisabledThreadIndex = 64;

    // btGetCurrentThreadIndex in BulletSharp 0.11.1's Windows x64 libbulletc (Bullet 2.87,
    // BT_THREADSAFE). -1 marks a byte that varies (a RIP-relative displacement or a short jump).
    private static readonly short[] s_signature =
    [
        0x8B, 0x0D, -1, -1, -1, -1,                         // mov ecx,[_tls_index]
        0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0x00, 0x00, 0x00, // mov rax,gs:[58h]
        0xBA, 0x04, 0x00, 0x00, 0x00,                       // mov edx,4
        0x48, 0x8B, 0x04, 0xC8,                             // mov rax,[rax+rcx*8]
        0x48, 0x03, 0xD0,                                   // add rdx,rax
        0x8B, 0x02,                                         // mov eax,[rdx]  (sThreadIndex)
        0x83, 0xF8, 0xFF,                                   // cmp eax,-1
        0x75, -1,                                           // jne ret
        0xB9, 0x01, 0x00, 0x00, 0x00,                       // mov ecx,1
        0x66, 0x0F, 0x1F, 0x84, 0x00, 0x00, 0x00, 0x00, 0x00, // nop
        0x33, 0xC0,                                         // xor eax,eax
        0xF0, 0x0F, 0xB1, 0x0D, -1, -1, -1, -1,             // lock cmpxchg [mutex],ecx
        0x75, 0xF4,                                         // jne spin
        0x8B, 0x05, -1, -1, -1, -1,                         // mov eax,[counter]
        0xFF, 0xC0,                                         // inc eax
        0x83, 0xF8, 0x40,                                   // cmp eax,64
        0x0F, 0x43, 0xC1,                                   // cmovae eax,ecx  (wrap to 1)
        0x33, 0xC9,                                         // xor ecx,ecx
        0x89, 0x05, -1, -1, -1, -1,                         // mov [counter],eax
        0x87, 0x0D, -1, -1, -1, -1,                         // xchg ecx,[mutex]
        0x89, 0x02,                                         // mov [rdx],eax
        0xC3                                                // ret
    ];

    // mov eax,64 ; ret
    private static readonly byte[] s_patch = [0xB8, DisabledThreadIndex, 0x00, 0x00, 0x00, 0xC3];

    private static readonly object s_sync = new();
    private static volatile BulletProfilerGuardStatus s_status = BulletProfilerGuardStatus.NotRun;
    private static string s_detail = "";
    private static nint s_function;

    public static BulletProfilerGuardStatus Status => s_status;

    public static string Detail => s_detail;

    // Idempotent and thread-safe; never throws.
    public static void EnsureApplied()
    {
        if (s_status != BulletProfilerGuardStatus.NotRun)
        {
            return;
        }

        lock (s_sync)
        {
            if (s_status != BulletProfilerGuardStatus.NotRun)
            {
                return;
            }

            BulletProfilerGuardStatus status;
            string detail;
            try
            {
                status = Apply(out detail);
            }
            catch (Exception ex)
            {
                status = BulletProfilerGuardStatus.Failed;
                detail = $"{ex.GetType().Name}: {ex.Message}";
            }

            s_detail = detail;
            s_status = status;
            if (status is BulletProfilerGuardStatus.Unsupported or BulletProfilerGuardStatus.Failed)
            {
                Console.Error.WriteLine($"[physics.bullet-profiler-guard] {status}: {detail}");
            }
        }
    }

    // Calls the native function on the current thread: DisabledThreadIndex once patched, the
    // thread's real profiler index when the guard is turned off. Test seam.
    internal static unsafe int ReadCurrentThreadIndexForTest()
    {
        if (s_function == 0)
        {
            throw new InvalidOperationException("The guard has not located the function.");
        }

        return ((delegate* unmanaged<int>)s_function)();
    }

    private static unsafe BulletProfilerGuardStatus Apply(out string detail)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            detail = $"only the Windows x64 libbulletc is patched ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture})";
            return BulletProfilerGuardStatus.Unsupported;
        }

        // The same resolution BulletSharp's DllImport uses, so this is the module it calls into.
        if (!NativeLibrary.TryLoad("libbulletc", typeof(BulletSharp.DiscreteDynamicsWorld).Assembly, null, out nint module))
        {
            detail = "libbulletc could not be loaded";
            return BulletProfilerGuardStatus.Unsupported;
        }

        if (!TryFindTextSection(module, out byte* text, out int textLength))
        {
            detail = "libbulletc has no readable .text section";
            return BulletProfilerGuardStatus.Unsupported;
        }

        byte* match = null;
        int matches = 0;
        int last = textLength - s_signature.Length;
        for (int i = 0; i <= last; i++)
        {
            if (Matches(text + i))
            {
                match = text + i;
                matches++;
            }
        }

        if (matches != 1)
        {
            detail = $"expected one btGetCurrentThreadIndex, found {matches}";
            return BulletProfilerGuardStatus.Unsupported;
        }

        s_function = (nint)match;
        if (Environment.GetEnvironmentVariable("DREAM_BULLET_PROFILER_GUARD") == "0")
        {
            // Located but left alone, so a test can show the crash this guard prevents.
            detail = "DREAM_BULLET_PROFILER_GUARD=0";
            return BulletProfilerGuardStatus.Disabled;
        }

        // The function starts 16-byte aligned (MSVC pads functions), so its first 8 bytes are one
        // aligned quadword and can be replaced atomically.
        if (((nint)match & (PatchWordSize - 1)) != 0)
        {
            detail = $"btGetCurrentThreadIndex at libbulletc+0x{(long)match - module:X} is not {PatchWordSize}-byte aligned";
            return BulletProfilerGuardStatus.Unsupported;
        }

        if (!VirtualProtect(match, PatchWordSize, PageExecuteReadWrite, out uint oldProtect))
        {
            detail = $"VirtualProtect failed ({Marshal.GetLastPInvokeError()})";
            return BulletProfilerGuardStatus.Failed;
        }

        long original = *(long*)match;
        long patched = original;
        byte* patchedBytes = (byte*)&patched;
        for (int i = 0; i < s_patch.Length; i++)
        {
            patchedBytes[i] = s_patch[i];
        }

        Interlocked.Exchange(ref *(long*)match, patched);
        VirtualProtect(match, PatchWordSize, oldProtect, out _);
        FlushInstructionCache(GetCurrentProcess(), match, PatchWordSize);
        detail = $"btGetCurrentThreadIndex at libbulletc+0x{(long)match - module:X} now returns {DisabledThreadIndex}";
        return BulletProfilerGuardStatus.Patched;
    }

    private static unsafe bool Matches(byte* code)
    {
        for (int i = 0; i < s_signature.Length; i++)
        {
            short expected = s_signature[i];
            if (expected >= 0 && code[i] != (byte)expected)
            {
                return false;
            }
        }

        return true;
    }

    // Reads the loaded image's PE headers (a module handle is its base address on Windows).
    private static unsafe bool TryFindTextSection(nint module, out byte* text, out int length)
    {
        text = null;
        length = 0;
        byte* image = (byte*)module;
        if (image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            return false;
        }

        byte* nt = image + *(int*)(image + 0x3C);
        if (*(uint*)nt != 0x00004550) // "PE\0\0"
        {
            return false;
        }

        ushort sectionCount = *(ushort*)(nt + 6);
        ushort optionalHeaderSize = *(ushort*)(nt + 20);
        byte* section = nt + 24 + optionalHeaderSize;
        for (int i = 0; i < sectionCount; i++, section += 40)
        {
            if (section[0] == (byte)'.' && section[1] == (byte)'t' && section[2] == (byte)'e'
                && section[3] == (byte)'x' && section[4] == (byte)'t' && section[5] == 0)
            {
                uint virtualSize = *(uint*)(section + 8);
                uint virtualAddress = *(uint*)(section + 12);
                text = image + virtualAddress;
                length = (int)virtualSize;
                return length > 0;
            }
        }

        return false;
    }

    private const uint PageExecuteReadWrite = 0x40;

    // One aligned quadword: the patch plus the original bytes after it, stored at once.
    private const int PatchWordSize = 8;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    private static extern unsafe bool FlushInstructionCache(nint process, void* address, nuint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
