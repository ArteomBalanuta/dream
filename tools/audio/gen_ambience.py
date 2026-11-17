"""Two seamless ambience loops for dream, 22.05 kHz mono: wind and night. Original synthesis only.

Owned by the atmosphere workstream. Run through build_all.py. Output: dream/audio/ambience/wind.wav
and night.wav. Loops are periodic by construction (whole-cycle LFOs, circular filtering, wrapped
events), so the end meets the start with no click.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import add_at, bandpass, lowpass, noise, rng_for, write_wav

AMB_SR = 22050


def _periodic_lfo(n: int, sr: int, rng: np.random.Generator, seconds: float, count: int, depth: float) -> np.ndarray:
    t = np.arange(n) / sr
    lfo = np.zeros(n)
    for _ in range(count):
        cycles = int(rng.integers(1, 7))
        lfo += rng.uniform(0.4, 1.0) * np.sin(2 * np.pi * cycles * t / seconds + rng.uniform(0, 6.28))
    lfo /= max(1e-9, np.max(np.abs(lfo)))
    return 1.0 + depth * lfo


def wind_loop() -> np.ndarray:
    rng = rng_for("wind")
    seconds = 8.0
    n = int(seconds * AMB_SR)
    base = noise(n, rng)
    low = bandpass(base, 90, 420, 2, AMB_SR) * 1.2
    mid = bandpass(noise(n, rng), 400, 1400, 2, AMB_SR) * 0.55
    howl = bandpass(noise(n, rng), 700, 900, 3, AMB_SR) * 0.35
    out = low * _periodic_lfo(n, AMB_SR, rng, seconds, 4, 0.45)
    out += mid * _periodic_lfo(n, AMB_SR, rng, seconds, 4, 0.7)
    out += howl * _periodic_lfo(n, AMB_SR, rng, seconds, 3, 0.9)
    y = out - np.mean(out)
    return y / np.max(np.abs(y)) * 0.55


def night_loop() -> np.ndarray:
    rng = rng_for("night")
    seconds = 8.0
    n = int(seconds * AMB_SR)
    t = np.arange(n) / AMB_SR
    drone = np.zeros(n)
    for f, a in ((55.0, 1.0), (82.5, 0.55), (110.25, 0.35), (164.75, 0.18)):
        drone += a * np.sin(2 * np.pi * f * t) * _periodic_lfo(n, AMB_SR, rng, seconds, 2, 0.25)
    drone = lowpass(drone, 400, 2, AMB_SR) * 0.35
    crickets = np.zeros(n)
    for _ in range(26):
        centre = rng.uniform(4000, 5200) / 2.0  # stays under Nyquist at 22.05 kHz
        start = int(rng.uniform(0, seconds) * AMB_SR)
        pulses = int(rng.integers(3, 6))
        for p in range(pulses):
            ln = int(0.022 * AMB_SR)
            tt = np.arange(ln) / AMB_SR
            chirp = np.sin(2 * np.pi * centre * 2 * tt + 0.0) * np.hanning(ln)
            add_at(crickets, chirp * rng.uniform(0.04, 0.12), start + int(p * 0.035 * AMB_SR), wrap=True)
    crickets = lowpass(crickets, 9000, 2, AMB_SR)
    out = drone + crickets
    out -= np.mean(out)
    return out / np.max(np.abs(out)) * 0.5



def build(out_root: Path) -> list[Path]:
    amb = out_root / "ambience"
    written: list[Path] = []
    for name, fn in (("wind", wind_loop), ("night", night_loop)):
        p = amb / f"{name}.wav"
        write_wav(p, fn(), AMB_SR)
        written.append(p)
    return written
