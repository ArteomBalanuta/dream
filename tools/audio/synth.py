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
