namespace Dream.Rendering.Hud;

// The HUD's "receiving world" line. During a world transfer (a join or a resync, sent in chunks)
// the client holds every message until the final chunk, so remote actors freeze until it ends;
// the line tells the player why. Local prediction and input carry on as usual.
//
// The receiver only learns the chunk count at the final chunk, so the line shows the bytes
// received and the time so far rather than a percentage. It appears only once the transfer has
// run for ShowThreshold, so a join on a fast link never flashes it.
public readonly record struct WorldTransferHudState(
    bool IsReceiving,
    long BytesReceived,
    int ChunksReceived,
    TimeSpan Elapsed)
{
    public static readonly TimeSpan ShowThreshold = TimeSpan.FromMilliseconds(300);

    // Shown under the progress line while the indicator is visible.
    public const string DetailLine = "Other players are paused until the world arrives.";

    public static WorldTransferHudState None => default;

    public bool IsVisible => IsReceiving && Elapsed >= ShowThreshold;

    public long KilobytesReceived => Math.Max(0L, BytesReceived) / 1024L;

    public long WholeSecondsElapsed => Elapsed <= TimeSpan.Zero ? 0L : (long)Elapsed.TotalSeconds;

    // "Receiving world... 412 KB, 6 s", as UTF-8 into the destination, without allocating.
    // False when the destination is too small.
    public bool TryFormat(Span<byte> destination, out int bytesWritten)
    {
        long kilobytes = KilobytesReceived;
        long seconds = WholeSecondsElapsed;
        return HudText.TryWrite(destination, $"Receiving world... {kilobytes} KB, {seconds} s", out bytesWritten);
    }

    public string Format()
    {
        return $"Receiving world... {KilobytesReceived} KB, {WholeSecondsElapsed} s";
    }
}
