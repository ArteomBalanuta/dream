"""Shared synthesis helpers for the procedural audio (numpy + stdlib only, fixed seeds).

Every sound is original: noise, sines, additive partials and spectral filtering. Nothing is
sampled or downloaded. Filters run in the frequency domain on stationary signals (noise is
filtered before it is enveloped), so a transient never gets pre-ringing and a periodic signal
filtered over its whole length stays periodic (loops use that for seamless seams).
"""
from __future__ import annotations

import wave
import zlib
from pathlib import Path

import numpy as np

SR = 44100


def rng_for(name: str) -> np.random.Generator:
    return np.random.default_rng(zlib.crc32(name.encode("utf-8")))


def samples(seconds: float, sr: int = SR) -> int:
    return int(round(seconds * sr))


def tvec(n: int, sr: int = SR) -> np.ndarray:
    return np.arange(n) / sr


def _freqs(n: int, sr: int) -> np.ndarray:
    return np.fft.rfftfreq(n, 1.0 / sr)


def lowpass(x: np.ndarray, fc: float, order: int = 2, sr: int = SR) -> np.ndarray:
    f = _freqs(len(x), sr)
    mask = 1.0 / np.sqrt(1.0 + (f / fc) ** (2 * order))
    return np.fft.irfft(np.fft.rfft(x) * mask, len(x))


def highpass(x: np.ndarray, fc: float, order: int = 2, sr: int = SR) -> np.ndarray:
    f = _freqs(len(x), sr)
    f[0] = 1e-6
    mask = 1.0 / np.sqrt(1.0 + (fc / f) ** (2 * order))
    mask[0] = 0.0
    return np.fft.irfft(np.fft.rfft(x) * mask, len(x))


def bandpass(x: np.ndarray, lo: float, hi: float, order: int = 2, sr: int = SR) -> np.ndarray:
    return highpass(lowpass(x, hi, order, sr), lo, order, sr)


def noise(n: int, rng: np.random.Generator) -> np.ndarray:
    return rng.standard_normal(n)


def decay(n: int, tau: float, sr: int = SR, attack: float = 0.002) -> np.ndarray:
    t = tvec(n, sr)
    env = np.exp(-t / tau)
    if attack > 0:
        env *= np.minimum(1.0, t / attack)
    return env


def sweep(f0: float, f1: float, n: int, curve: float = 3.0) -> np.ndarray:
    """Frequency track from f0 to f1 with an exponential-looking approach."""
    u = np.linspace(0.0, 1.0, n)
    k = 1.0 - np.exp(-curve * u)
    k /= k[-1] if k[-1] else 1.0
    return f0 + (f1 - f0) * k


def sine(freq, n: int, phase: float = 0.0, sr: int = SR) -> np.ndarray:
    f = np.broadcast_to(np.asarray(freq, dtype=np.float64), (n,))
    return np.sin(phase + 2 * np.pi * np.cumsum(f) / sr)


def formant_voice(f0: np.ndarray, formants: list[tuple[float, float, float]], n: int,
                  harmonics: int = 36, tilt: float = 1.2, sr: int = SR) -> np.ndarray:
    """Additive glottal-ish voice: harmonics of a moving f0 weighted by Gaussian formants."""
    out = np.zeros(n)
    phase = 2 * np.pi * np.cumsum(f0) / sr
    for k in range(1, harmonics + 1):
        fk = f0 * k
        gain = np.zeros(n)
        for centre, width, amp in formants:
            gain += amp * np.exp(-0.5 * ((fk - centre) / width) ** 2)
        gain += 0.03
        gain *= 1.0 / (k ** tilt)
        gain[fk > sr * 0.45] = 0.0
        out += gain * np.sin(k * phase)
    return out


def bell(freq: float, seconds: float, rng: np.random.Generator, sr: int = SR,
         ratios=(1.0, 2.0, 2.41, 3.0, 4.18, 5.43), decays=(1.0, 0.7, 0.5, 0.4, 0.25, 0.15),
         amps=(1.0, 0.6, 0.45, 0.35, 0.25, 0.15)) -> np.ndarray:
    n = samples(seconds, sr)
    t = tvec(n, sr)
    out = np.zeros(n)
    for r, d, a in zip(ratios, decays, amps):
        detune = 1.0 + rng.uniform(-0.002, 0.002)
        out += a * np.sin(2 * np.pi * freq * r * detune * t + rng.uniform(0, 6.28)) * np.exp(-t / (seconds * 0.35 * d))
    return out


def reverb_ir(seconds: float, rng: np.random.Generator, sr: int = SR, damp: float = 3500.0) -> np.ndarray:
    n = samples(seconds, sr)
    ir = rng.standard_normal(n) * np.exp(-tvec(n, sr) / (seconds / 5.5))
    ir = lowpass(ir, damp, 1, sr)
    ir /= np.sqrt(np.sum(ir ** 2))
    return ir


def circular_reverb(x: np.ndarray, ir: np.ndarray, wet: float) -> np.ndarray:
    """Circular convolution: a periodic input stays periodic (loops stay seamless)."""
    n = len(x)
    ir_p = np.zeros(n)
    ir_p[: min(n, len(ir))] = ir[:n]
    y = np.fft.irfft(np.fft.rfft(x) * np.fft.rfft(ir_p), n)
    return x * (1.0 - wet) + y * wet


def add_at(buf: np.ndarray, sig: np.ndarray, start: int, wrap: bool = False) -> None:
    n = len(buf)
    if wrap:
        idx = (start + np.arange(len(sig))) % n
        np.add.at(buf, idx, sig)
        return
    end = min(n, start + len(sig))
    if end > start:
        buf[start:end] += sig[: end - start]


def finalize(x: np.ndarray, peak: float = 0.85, fade_in: float = 0.003, fade_out: float = 0.02,
             sr: int = SR) -> np.ndarray:
    """Remove DC, normalise to `peak`, and fade the ends so a one-shot never clicks."""
    y = x - np.mean(x)
    m = np.max(np.abs(y)) or 1.0
    y = y / m * peak
    fi = min(len(y) // 2, samples(fade_in, sr))
    fo = min(len(y) // 2, samples(fade_out, sr))
    if fi:
        y[:fi] *= np.linspace(0, 1, fi) ** 2
    if fo:
        y[-fo:] *= np.linspace(1, 0, fo) ** 2
    return y


def write_wav(path: Path, data: np.ndarray, sr: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    arr = np.clip(data, -1.0, 1.0)
    pcm = np.round(arr * 32767.0).astype("<i2")
    channels = 1 if pcm.ndim == 1 else pcm.shape[1]
    with wave.open(str(path), "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm.tobytes())


# ---------------------------------------------------------------- helpers for the gothic pack
# Added for the per-family generators (sfx_combat, sfx_wolf, gen_music, gen_ambience). The helpers
# above stay byte-for-byte as they were: the legacy sounds in gen_sfx.py must not change.

def fir_convolve(x: np.ndarray, ir: np.ndarray) -> np.ndarray:
    """Linear (not circular) convolution via FFT. Output length is len(x) + len(ir) - 1."""
    x = np.asarray(x, dtype=np.float64)
    ir = np.asarray(ir, dtype=np.float64)
    m = len(x) + len(ir) - 1
    size = 1 << max(0, (m - 1).bit_length())
    return np.fft.irfft(np.fft.rfft(x, size) * np.fft.rfft(ir, size), size)[:m]


def room_ir(seconds: float, rng: np.random.Generator, sr: int = SR, damp: float = 3500.0,
            predelay: float = 0.0, early=()) -> np.ndarray:
    """Mono room impulse response (wet path only, no direct sound).

    `predelay` seconds of silence, then the diffuse tail (exponential noise, about -48 dB at
    `seconds`, darkened by a one-pole lowpass at `damp` Hz, normalised to unit energy). `early` is a
    sequence of (delay_s, gain) discrete reflections added at predelay + delay_s, gain relative to
    a direct sound of 1.0. Length is predelay + seconds.
    """
    pre = samples(predelay, sr)
    n = samples(seconds, sr)
    tail = rng.standard_normal(n) * np.exp(-tvec(n, sr) / (seconds / 5.5))
    tail = lowpass(tail, damp, 1, sr)
    tail /= np.sqrt(np.sum(tail ** 2)) or 1.0
    ir = np.zeros(pre + n)
    ir[pre:] = tail
    for delay_s, gain in early:
        i = pre + samples(delay_s, sr)
        if 0 <= i < len(ir):
            ir[i] += gain
    return ir


def modal(freqs, decays, amps, n: int, rng: np.random.Generator, sr: int = SR) -> np.ndarray:
    """Sum of exponentially decaying sines (impact resonances of wood, bone, steel).

    freqs in Hz, decays are time constants in seconds, amps linear; each mode gets a random start
    phase from `rng`. Modes at or above 0.45 * sr are skipped (their phase is still drawn).
    """
    t = tvec(n, sr)
    out = np.zeros(n)
    for f, d, a in zip(freqs, decays, amps):
        phase = rng.uniform(0.0, 2 * np.pi)
        if f >= 0.45 * sr:
            continue
        out += a * np.sin(2 * np.pi * f * t + phase) * np.exp(-t / max(d, 1e-6))
    return out


def env_adsr(n: int, a: float, d: float, s: float, r: float, sr: int = SR) -> np.ndarray:
    """Linear ADSR envelope of exactly n samples: attack a s up to 1, decay d s down to level s,
    hold, then release r s to 0 at the end. If a + d + r is longer than n, they are scaled to fit."""
    na, nd, nr = samples(a, sr), samples(d, sr), samples(r, sr)
    total = na + nd + nr
    if total > n:
        k = n / total
        na, nd = int(na * k), int(nd * k)
        nr = n - na - nd
    nh = n - na - nd - nr
    return np.concatenate([
        np.linspace(0.0, 1.0, na, endpoint=False),
        np.linspace(1.0, s, nd, endpoint=False),
        np.full(nh, float(s)),
        np.linspace(s, 0.0, nr),
    ])[:n]


def _spectral_noise(n: int, rng: np.random.Generator, power: float) -> np.ndarray:
    spec = np.fft.rfft(rng.standard_normal(n))
    f = np.arange(len(spec), dtype=np.float64)
    f[0] = 1.0
    spec /= f ** (power / 2.0)
    spec[0] = 0.0
    y = np.fft.irfft(spec, n)
    return y / (np.sqrt(np.mean(y ** 2)) or 1.0)


def pink(n: int, rng: np.random.Generator) -> np.ndarray:
    """1/f (pink) noise, zero mean, unit RMS. Built spectrally, so it is periodic over n."""
    return _spectral_noise(n, rng, 1.0)


def brown(n: int, rng: np.random.Generator) -> np.ndarray:
    """1/f^2 (brown) noise, zero mean, unit RMS. Built spectrally, so it is periodic over n."""
    return _spectral_noise(n, rng, 2.0)


def soft_clip(x: np.ndarray, drive: float) -> np.ndarray:
    """tanh saturation scaled so +-1 still maps to +-1: tanh(drive * x) / tanh(drive). drive > 0."""
    return np.tanh(drive * np.asarray(x)) / np.tanh(drive)


def onepole_lowpass_tv(x: np.ndarray, fc_track, sr: int = SR, wrap: bool = False) -> np.ndarray:
    """One-pole lowpass whose cutoff follows a per-sample array `fc_track` (Hz; a scalar works too).

    y[i] = y[i-1] + g[i] * (x[i] - y[i-1]) with g = 1 - exp(-2 pi fc / sr). With wrap=True the
    filter runs over x twice and returns the second pass, so the state at the start matches the
    end (for seamless loops).
    """
    x = np.asarray(x, dtype=np.float64)
    fc = np.broadcast_to(np.asarray(fc_track, dtype=np.float64), x.shape)
    g = (1.0 - np.exp(-2 * np.pi * np.clip(fc, 0.0, sr * 0.49) / sr)).tolist()
    xs = x.tolist()
    y = [0.0] * len(xs)
    state = 0.0
    for _ in range(2 if wrap else 1):
        for i, (xi, gi) in enumerate(zip(xs, g)):
            state += gi * (xi - state)
            y[i] = state
    return np.asarray(y)


def resonator_tv(x: np.ndarray, fc_track, q: float, sr: int = SR, wrap: bool = False) -> np.ndarray:
    """Band-pass state-variable filter whose centre follows a per-sample array `fc_track` (Hz).

    Gain is about 1 at the centre; a higher `q` is narrower and rings longer (formants, whistles,
    wind howl). Centres are clamped to sr/6 for stability. wrap=True runs two passes for loops.
    """
    x = np.asarray(x, dtype=np.float64)
    fc = np.broadcast_to(np.asarray(fc_track, dtype=np.float64), x.shape)
    f = (2.0 * np.sin(np.pi * np.clip(fc, 1.0, sr / 6.0) / sr)).tolist()
    damp = 1.0 / max(q, 0.5)
    xs = x.tolist()
    y = [0.0] * len(xs)
    low = band = 0.0
    for _ in range(2 if wrap else 1):
        for i, (xi, fi) in enumerate(zip(xs, f)):
            low += fi * band
            high = xi - low - damp * band
            band += fi * high
            y[i] = band * damp
    return np.asarray(y)


def jitter(n: int, rng: np.random.Generator, amount: float, rate_hz: float, sr: int = SR) -> np.ndarray:
    """Smoothed random multiplier around 1.0 (f0 jitter, amplitude shimmer).

    Random targets about `rate_hz` times a second joined by smoothstep curves; the result is
    1 + amount * u with |u| <= 1.
    """
    step = max(1, int(round(sr / max(rate_hz, 1e-6))))
    points = rng.uniform(-1.0, 1.0, n // step + 2)
    pos = np.arange(n) / step
    i = pos.astype(np.int64)
    u = pos - i
    u = u * u * (3.0 - 2.0 * u)
    return 1.0 + amount * (points[i] * (1.0 - u) + points[i + 1] * u)


def spectrogram_png(x: np.ndarray, sr: int, path: Path, width: int = 900, height: int = 300,
                    floor_db: float = -90.0) -> None:
    """Write a log-frequency spectrogram (30 Hz .. sr/2, dB, bright = loud) as a PNG via Pillow.

    Stereo input (n, 2) is mixed to mono. For inspecting a sound by eye: tonal lines, noise bands,
    decays. Write it under artifacts/ (gitignored), never into dream/audio.
    """
    from PIL import Image

    x = np.asarray(x, dtype=np.float64)
    mono = x.mean(axis=1) if x.ndim == 2 else x
    win = 2048
    if len(mono) < win:
        mono = np.pad(mono, (0, win - len(mono)))
    hops = np.linspace(0, len(mono) - win, width).astype(np.int64)
    frames = np.stack([mono[h:h + win] for h in hops]) * np.hanning(win)
    mag = np.abs(np.fft.rfft(frames, axis=1))
    freqs = np.fft.rfftfreq(win, 1.0 / sr)
    rows = np.geomspace(30.0, sr / 2.0, height)[::-1]
    cols = np.stack([np.interp(rows, freqs, m) for m in mag], axis=1)
    db = 20 * np.log10(cols / (np.max(cols) or 1.0) + 1e-12)
    v = np.clip((db - floor_db) / -floor_db, 0.0, 1.0)
    rgb = np.stack([v ** 0.8, v ** 1.6, v ** 3.0], axis=2)
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray((rgb * 255).astype(np.uint8), "RGB").save(path)


def _self_check() -> None:
    import tempfile

    rng = np.random.default_rng(1)
    x = rng.standard_normal(1000)
    ir = rng.standard_normal(37)
    y = fir_convolve(x, ir)
    assert len(y) == 1036 and np.allclose(y, np.convolve(x, ir)), "fir_convolve"
    r = room_ir(0.5, rng, predelay=0.01, early=((0.005, 0.5),))
    assert len(r) == samples(0.51) and np.all(r[:samples(0.01)] == 0.0), "room_ir"
    m = modal((200.0, 531.0, 30000.0), (0.1, 0.05, 1.0), (1.0, 0.5, 9.0), 4410, rng)
    assert len(m) == 4410 and np.max(np.abs(m)) <= 1.5 and abs(m[-1]) < 0.4, "modal"
    e = env_adsr(1000, 0.001, 0.002, 0.5, 0.003)
    assert len(e) == 1000 and e[0] == 0.0 and abs(e[-1]) < 1e-9 and abs(e[500] - 0.5) < 1e-9, "env_adsr"
    assert len(env_adsr(10, 1.0, 1.0, 0.5, 1.0)) == 10, "env_adsr fit"
    for fn in (pink, brown):
        p = fn(4096, rng)
        assert len(p) == 4096 and abs(np.mean(p)) < 1e-9 and abs(np.sqrt(np.mean(p ** 2)) - 1) < 1e-9, fn.__name__
    s = soft_clip(np.array([-10.0, -1.0, 0.0, 1.0]), 3.0)
    assert abs(s[1] + 1) < 1e-12 and s[2] == 0.0 and abs(s[3] - 1) < 1e-12 and s[0] > -1.01, "soft_clip"
    lp = onepole_lowpass_tv(np.ones(2000), np.linspace(50, 5000, 2000))
    assert len(lp) == 2000 and 0.9 < lp[-1] <= 1.0, "onepole_lowpass_tv"
    w = onepole_lowpass_tv(np.sin(2 * np.pi * 5 * np.arange(4410) / SR), 200.0, wrap=True)
    assert abs(w[0] - w[-1]) < 0.01, "onepole_lowpass_tv wrap"
    tone = np.sin(2 * np.pi * 1000 * tvec(8820))
    on = np.max(np.abs(resonator_tv(tone, 1000.0, 8.0)[4410:]))
    off = np.max(np.abs(resonator_tv(tone, 4000.0, 8.0)[4410:]))
    assert 0.5 < on < 1.5 and off < on * 0.3, "resonator_tv"
    j = jitter(5000, rng, 0.02, 7.0)
    assert len(j) == 5000 and np.all(np.abs(j - 1.0) <= 0.02 + 1e-12) and np.std(j) > 0, "jitter"
    with tempfile.TemporaryDirectory() as d:
        out = Path(d) / "spec.png"
        spectrogram_png(tone, SR, out)
        assert out.stat().st_size > 0, "spectrogram_png"
    print("synth self-check ok")


if __name__ == "__main__":
    _self_check()
