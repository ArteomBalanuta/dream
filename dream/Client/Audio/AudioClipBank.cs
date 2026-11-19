using Dream.Assets;

namespace Dream.Client.Audio;

// The decoded clips, loaded once from the audio folder next to the executable:
//   sfx/<stem>.wav or sfx/<stem>_1.wav, _2.wav, ...   one-shots (variants are picked at random)
//   music/{menu,world}.wav, ambience/{wind,night}.wav  loops
// A missing or broken file is skipped (that sound stays silent) and reported through `report`.
public sealed class AudioClipBank
{
    private readonly WavClip[]?[] _oneShots = new WavClip[SoundIds.Count][];
    private readonly Dictionary<AudioLoopId, WavClip> _loops = new();

    public static string DefaultDirectory => AssetPaths.AudioDirectory;

    public int OneShotCount
    {
        get
        {
            int count = 0;
            foreach (WavClip[]? variants in _oneShots)
            {
                count += variants?.Length ?? 0;
            }

            return count;
        }
    }

    public int LoopCount => _loops.Count;

    public static AudioClipBank Load(string directory, Action<string>? report = null)
    {
        AudioClipBank bank = new();
        for (int i = 0; i < SoundIds.Count; i++)
        {
            SoundId id = (SoundId)i;
            string stem = SoundIds.FileStem(id);
            List<WavClip> variants = [];
            TryAdd(variants, Path.Combine(directory, "sfx", stem + ".wav"), report);
            for (int v = 1; v <= 8; v++)
            {
                TryAdd(variants, Path.Combine(directory, "sfx", $"{stem}_{v}.wav"), report);
            }

            if (variants.Count > 0)
            {
                bank._oneShots[i] = [.. variants];
            }
        }

        AddLoop(bank, AudioLoopId.MusicMenu, Path.Combine(directory, "music", "menu.wav"), report);
        AddLoop(bank, AudioLoopId.MusicWorld, Path.Combine(directory, "music", "world.wav"), report);
        AddLoop(bank, AudioLoopId.Wind, Path.Combine(directory, "ambience", "wind.wav"), report);
        AddLoop(bank, AudioLoopId.Night, Path.Combine(directory, "ambience", "night.wav"), report);
        return bank;
    }

    // A bank built in memory, for tests.
    public static AudioClipBank FromClips(IReadOnlyDictionary<SoundId, WavClip[]> oneShots, IReadOnlyDictionary<AudioLoopId, WavClip>? loops = null)
    {
        AudioClipBank bank = new();
        foreach ((SoundId id, WavClip[] variants) in oneShots)
        {
            bank._oneShots[(int)id] = variants;
        }

        if (loops is not null)
        {
            foreach ((AudioLoopId id, WavClip clip) in loops)
            {
                bank._loops[id] = clip;
            }
        }

        return bank;
    }

    public bool TryGet(SoundId id, Random random, out WavClip clip)
    {
        WavClip[]? variants = (uint)id < (uint)_oneShots.Length ? _oneShots[(int)id] : null;
        if (variants is null || variants.Length == 0)
        {
            clip = null!;
            return false;
        }

        clip = variants.Length == 1 ? variants[0] : variants[random.Next(variants.Length)];
        return true;
    }

    public bool TryGetLoop(AudioLoopId id, out WavClip clip) => _loops.TryGetValue(id, out clip!);

    private static void TryAdd(List<WavClip> into, string path, Action<string>? report)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            into.Add(WavClip.Load(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            report?.Invoke($"[audio] skipped {path}: {ex.Message}");
        }
    }

    private static void AddLoop(AudioClipBank bank, AudioLoopId id, string path, Action<string>? report)
    {
        List<WavClip> one = [];
        TryAdd(one, path, report);
        if (one.Count == 1)
        {
            bank._loops[id] = one[0];
        }
    }
}
