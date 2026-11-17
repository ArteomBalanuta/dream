"""Two seamless ambience loops for dream, 22.05 kHz mono: wind and night. Original synthesis only.

Wind: cold wind over ruins and a moor. Irregular gusts (fast rise, slow fall, random spacing and
strength) drive narrow whistling resonances whose pitch and level follow the gust, a dry grass
rustle made of granular micro-events, and a restrained low rumble. There is no broadband swell: the
body of the sound is pitched and dry.
Night: several field crickets at different distances (3.5-5 kHz carriers, chirps of 3-5 pulses at
about 30 Hz), at most one distant owl, very faint cold air.
Both loops are periodic by construction (wrapped events, circular filtering, wrap=True on the
time-varying filters), so the end meets the start with no click.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (add_at, bandpass, brown, fir_convolve, highpass, jitter, lowpass, pink,
                   resonator_tv, rng_for, room_ir, soft_clip, write_wav)

AMB_SR = 22050
WIND_SECONDS = 16.0
NIGHT_SECONDS = 16.0


def _slow(n: int, rng: np.random.Generator, cutoff: float) -> np.ndarray:
    """Periodic smooth random curve (lowpassed white noise, circular), unit RMS."""
    y = lowpass(rng.standard_normal(n), cutoff, 2, AMB_SR)
    return y / (np.sqrt(np.mean(y ** 2)) or 1.0)


def gust_track(n: int, rng: np.random.Generator, seconds: float, count: int) -> np.ndarray:
    """Irregular gust strength 0..1: randomly spaced gusts, fast rise, slow fall, wrapped."""
    gaps = rng.uniform(0.3, 2.6, count)
    starts = np.cumsum(gaps) / np.sum(gaps) * seconds
    g = np.zeros(n)
    for s in starts:
        strength = rng.uniform(0.15, 1.0) ** 1.6
        rise = rng.uniform(0.3, 1.1)
        fall = rng.uniform(0.7, 2.2)
        m = int(min(seconds * 0.9, 6.0 * fall) * AMB_SR)
        t = np.arange(m) / AMB_SR
        env = (1.0 - np.exp(-t / (rise * 0.45))) ** 2 * np.exp(-t / fall)
        env /= np.max(env)
        add_at(g, strength * env, int(s * AMB_SR), wrap=True)
    g = 0.03 + g
    g = np.tanh(g * 1.1) / np.tanh(1.1 * np.max(g))
    return g / np.max(g)


def wind_loop() -> np.ndarray:
    rng = rng_for("wind")
    n = int(WIND_SECONDS * AMB_SR)
    g = gust_track(n, rng, WIND_SECONDS, 10)
    # Whistles: air over gaps in stone. Narrow resonances of pink noise; the pitch climbs with the
    # gust and wanders a little, the level grows much faster than the gust itself.
    out = np.zeros(n)
    for base, q, level in ((430.0, 30.0, 1.0), (690.0, 38.0, 0.8), (1120.0, 45.0, 0.45)):
        wander = 1.0 + 0.025 * _slow(n, rng, 1.2)
        fc = base * (0.66 + 0.7 * g) * wander
        w = resonator_tv(pink(n, rng), fc, q, AMB_SR, wrap=True)
        w /= np.sqrt(np.mean(w ** 2)) or 1.0
        out += level * 0.5 * w * g ** 2.2 * (1.0 + 0.25 * _slow(n, rng, 6.0))
    # Moan: a low hollow resonance, wider, only in the stronger gusts.
    moan = resonator_tv(brown(n, rng), 190.0 * (0.9 + 0.2 * g), 9.0, AMB_SR, wrap=True)
    moan /= np.sqrt(np.mean(moan ** 2)) or 1.0
    out += 0.5 * moan * g ** 1.6
    # Restrained rumble.
    rumble = lowpass(brown(n, rng), 140.0, 2, AMB_SR)
    out += 0.22 * rumble * (0.35 + 0.65 * g)
    # Dry grass and leaf rustle: sparse clicks (granular), more of them in a gust, 2-6 kHz.
    rate = 40.0 + 520.0 * g ** 1.5
    hits = (rng.random(n) < rate / AMB_SR) * rng.exponential(1.0, n)
    rustle = bandpass(hits, 2000.0, 6000.0, 2, AMB_SR)
    rustle = rustle / (np.sqrt(np.mean(rustle ** 2)) or 1.0)
    out += 0.17 * rustle * (0.2 + g)
    # A trace of air so the silence between gusts is not dead.
    out += 0.02 * highpass(pink(n, rng), 800.0, 1, AMB_SR)
    out = soft_clip(out / np.max(np.abs(out)) * 0.9, 1.3)
    y = out - np.mean(out)
    return y / np.max(np.abs(y)) * 0.6


def _cricket(n: int, rng: np.random.Generator, seconds: float, carrier: float, chirp_rate: float,
             pulses: int, level: float, distance: float) -> np.ndarray:
    """One cricket: its own carrier, chirp rhythm and drift; each chirp is a few damped pulses."""
    out = np.zeros(n)
    pulse_rate = rng.uniform(26.0, 34.0)
    pn = int(0.016 * AMB_SR)
    tt = np.arange(pn) / AMB_SR
    # Resonant pulse: fast attack, exponential ring-down, the carrier slightly lower at the tail.
    pulse = np.sin(2 * np.pi * carrier * tt * (1.0 - 0.01 * tt / 0.016)) * np.minimum(1.0, tt / 0.0015) * np.exp(-tt / 0.0045)
    period = 1.0 / chirp_rate
    t0 = rng.uniform(0.0, period)
    count = int(seconds * chirp_rate) + 1
    # Spread the chirp starts so they close the loop exactly: scale the irregular gaps to fill it.
    gaps = period * rng.uniform(0.75, 1.3, count)
    starts = t0 + np.cumsum(gaps) / np.sum(gaps) * seconds
    for s in starts:
        k = pulses + int(rng.integers(-1, 2))
        amp = rng.uniform(0.75, 1.0)
        for p in range(max(2, k)):
            shape = 1.0 - 0.18 * abs(p - (k - 1) / 2.0)
            at = s + p / pulse_rate * rng.uniform(0.96, 1.04)
            add_at(out, pulse * level * amp * shape * rng.uniform(0.9, 1.0), int(at * AMB_SR), wrap=True)
    # Distance: lose the highs, a little smeared by the open air.
    out = lowpass(out, 9000.0 / (1.0 + 1.5 * distance), 1, AMB_SR)
    return out


def _owl(rng: np.random.Generator) -> np.ndarray:
    """A distant tawny-owl-like call: a short 'hu' then a longer, falling 'hoooo', warm and breathy."""
    notes = ((0.0, 0.32, 410.0, 380.0), (0.55, 1.35, 400.0, 340.0))   # start, length, f0 start/end
    total = int(2.4 * AMB_SR)
    out = np.zeros(total)
    for start, length, f_a, f_b in notes:
        m = int(length * AMB_SR)
        u = np.linspace(0.0, 1.0, m)
        f0 = (f_a + (f_b - f_a) * u ** 1.4) * jitter(m, rng, 0.006, 9.0, AMB_SR)
        f0 = f0 * (1.0 + 0.004 * np.sin(2 * np.pi * 5.5 * u * length))
        phase = 2 * np.pi * np.cumsum(f0) / AMB_SR
        v = np.zeros(m)
        for k, a in ((1, 1.0), (2, 0.35), (3, 0.12), (4, 0.05)):
            v += a * np.sin(k * phase)
        v += 0.04 * lowpass(rng.standard_normal(m), 1800.0, 1, AMB_SR)
        env = np.minimum(1.0, np.arange(m) / (0.07 * AMB_SR)) ** 2 * np.minimum(1.0, (m - np.arange(m)) / (0.22 * AMB_SR))
        add_at(out, v * env, int(start * AMB_SR))
    return lowpass(out, 1500.0, 2, AMB_SR)


def night_loop() -> np.ndarray:
    rng = rng_for("night")
    n = int(NIGHT_SECONDS * AMB_SR)
    # (carrier Hz, chirp groups per second, pulses per chirp, level, distance 0 near .. 1 far)
    crickets = (
        (4650.0, 1.45, 4, 0.55, 0.5),
        (4120.0, 1.1, 5, 0.42, 0.9),
        (3720.0, 1.7, 3, 0.35, 1.2),
        (4880.0, 0.9, 4, 0.28, 1.5),
        (3560.0, 1.3, 5, 0.22, 2.0),
    )
    out = np.zeros(n)
    for carrier, rate, pulses, level, dist in crickets:
        out += _cricket(n, rng, NIGHT_SECONDS, carrier, rate, pulses, level, dist)
    # One distant owl, softened and given an open-air tail so it sits far away.
    owl = _owl(rng)
    tail = room_ir(1.6, rng, AMB_SR, 1400.0, predelay=0.09, early=((0.21, 0.25), (0.47, 0.12)))
    owl = np.concatenate([owl, np.zeros(len(tail))])
    owl = fir_convolve(owl, np.concatenate([[1.0], tail * 0.9]))
    add_at(out, owl * 0.07, int(6.2 * AMB_SR), wrap=True)
    # Very faint cold air: dark, dry, nearly still.
    out += 0.012 * bandpass(pink(n, rng), 250.0, 3000.0, 1, AMB_SR) * (1.0 + 0.3 * _slow(n, rng, 0.8))
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
