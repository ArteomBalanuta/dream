using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using ImGuiNET;

namespace Dream.Rendering.Hud;

// Plain HUD text without a managed string per frame. Callers stackalloc the UTF-8 buffer:
//   Span<byte> buffer = stackalloc byte[HudText.StackBufferBytes];
//   HudText.Text(buffer, $"Players: {count}");
// Formatting uses the current culture, exactly like $"". Use it only for plain text; widget
// labels that carry an ImGui "##id" suffix still go through the string overloads.
public static class HudText
{
    public const int StackBufferBytes = 256;

    private static ReadOnlySpan<byte> EmptyText => "\0"u8;

    public static void Text(
        Span<byte> buffer,
        [InterpolatedStringHandlerArgument(nameof(buffer))] ref HudTextInterpolatedStringHandler text)
    {
        if (text.TryGetUtf8(out ReadOnlySpan<byte> utf8))
        {
            TextUnformatted(utf8);
            return;
        }

        ImGui.Text(text.ToStringAndClear());
    }

    // Formats into the destination only. Returns false when the destination is too small; the
    // caller then formats a string instead.
    public static bool TryWrite(
        Span<byte> destination,
        [InterpolatedStringHandlerArgument(nameof(destination))] ref HudTextInterpolatedStringHandler text,
        out int bytesWritten)
    {
        if (text.TryGetUtf8(out ReadOnlySpan<byte> utf8))
        {
            bytesWritten = utf8.Length;
            return true;
        }

        bytesWritten = 0;
        return false;
    }

    public static unsafe void TextUnformatted(ReadOnlySpan<byte> utf8)
    {
        // A null text_end makes ImGui fall back to strlen, so an empty span still needs a real pointer.
        ReadOnlySpan<byte> source = utf8.IsEmpty ? EmptyText : utf8;
        fixed (byte* start = source)
        {
            ImGuiNative.igTextUnformatted(start, start + utf8.Length);
        }
    }
}

// Writes each part as UTF-8 into the caller's buffer: numbers through their own UTF-8 TryFormat
// (a direct call, so nothing is boxed even before the JIT optimizes), everything else through
// Utf8.TryWrite's handler. If the buffer runs out, it switches to a string builder so
// HudText.Text still shows the full text.
[InterpolatedStringHandler]
public ref struct HudTextInterpolatedStringHandler
{
    private readonly Span<byte> _destination;
    private int _position;
    private bool _usingFallback;
    private DefaultInterpolatedStringHandler _fallback;

    public HudTextInterpolatedStringHandler(int literalLength, int formattedCount, Span<byte> buffer)
    {
        _destination = buffer;
        _position = 0;
        _usingFallback = false;
        _fallback = default;
    }

    public void AppendLiteral(string value)
    {
        if (!_usingFallback)
        {
            Span<byte> remaining = _destination[_position..];
            Utf8.TryWriteInterpolatedStringHandler part = new(value.Length, 0, remaining, out bool shouldAppend);
            if (shouldAppend && part.AppendLiteral(value) && Utf8.TryWrite(remaining, ref part, out int written))
            {
                _position += written;
                return;
            }

            SwitchToFallback();
        }

        _fallback.AppendLiteral(value);
    }

    public void AppendFormatted<T>(T value)
    {
        AppendFormatted(value, format: null);
    }

    public void AppendFormatted<T>(T value, string? format)
    {
        if (!_usingFallback)
        {
            Span<byte> remaining = _destination[_position..];
            Utf8.TryWriteInterpolatedStringHandler part = new(0, 1, remaining, out bool shouldAppend);
            if (shouldAppend && part.AppendFormatted(value, format) && Utf8.TryWrite(remaining, ref part, out int written))
            {
                _position += written;
                return;
            }

            SwitchToFallback();
        }

        _fallback.AppendFormatted(value, format);
    }

    public void AppendFormatted(int value)
    {
        AppendNumber(value, format: null);
    }

    public void AppendFormatted(int value, string? format)
    {
        AppendNumber(value, format);
    }

    public void AppendFormatted(long value)
    {
        AppendNumber(value, format: null);
    }

    public void AppendFormatted(long value, string? format)
    {
        AppendNumber(value, format);
    }

    public void AppendFormatted(float value)
    {
        AppendNumber(value, format: null);
    }

    public void AppendFormatted(float value, string? format)
    {
        AppendNumber(value, format);
    }

    public void AppendFormatted(double value)
    {
        AppendNumber(value, format: null);
    }

    public void AppendFormatted(double value, string? format)
    {
        AppendNumber(value, format);
    }

    public void AppendFormatted(string? value)
    {
        AppendFormatted(value.AsSpan());
    }

    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (!_usingFallback)
        {
            Span<byte> remaining = _destination[_position..];
            Utf8.TryWriteInterpolatedStringHandler part = new(0, 1, remaining, out bool shouldAppend);
            if (shouldAppend && part.AppendFormatted(value) && Utf8.TryWrite(remaining, ref part, out int written))
            {
                _position += written;
                return;
            }

            SwitchToFallback();
        }

        _fallback.AppendFormatted(value);
    }

    private void AppendNumber<T>(T value, string? format)
        where T : IUtf8SpanFormattable, ISpanFormattable
    {
        if (!_usingFallback)
        {
            if (value.TryFormat(_destination[_position..], out int written, format, provider: null))
            {
                _position += written;
                return;
            }

            SwitchToFallback();
        }

        _fallback.AppendFormatted(value, format);
    }

    public readonly bool TryGetUtf8(out ReadOnlySpan<byte> utf8)
    {
        if (_usingFallback)
        {
            utf8 = default;
            return false;
        }

        utf8 = _destination[.._position];
        return true;
    }

    public string ToStringAndClear()
    {
        if (!_usingFallback)
        {
            string text = Encoding.UTF8.GetString(_destination[.._position]);
            _position = 0;
            return text;
        }

        _usingFallback = false;
        _position = 0;
        return _fallback.ToStringAndClear();
    }

    // A handler that falls back and is never turned into a string leaves its pooled buffer to the GC.
    private void SwitchToFallback()
    {
        _usingFallback = true;
        _fallback = new DefaultInterpolatedStringHandler(0, 0);
        _fallback.AppendLiteral(Encoding.UTF8.GetString(_destination[.._position]));
    }
}
