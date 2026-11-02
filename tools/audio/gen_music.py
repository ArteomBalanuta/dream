"""Two seamless music loops for dream, 22.05 kHz stereo, 20 s each. Original synthesis only.

World: a slow dark ambient drone with sparse minor-key plucks (A minor / phrygian colour).
Menu: a brooding organ and choir pad cycling D minor, Bb, G minor, A.
Both are built from components that are periodic over the loop (partial frequencies on a 0.05 Hz
grid, windows that wrap, circular reverb), so the end meets the start with no click.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (add_at, bandpass, circular_reverb, lowpass, noise, reverb_ir, rng_for,
                   write_wav)

MUSIC_SR = 22050
SECONDS = 20.0
N = int(SECONDS * MUSIC_SR)
T = np.arange(N) / MUSIC_SR


def _snap(freq: float) -> float:
    """Nearest frequency with a whole number of cycles in the loop."""
    return round(freq * SECONDS) / SECONDS


def _periodic_lfo(rng: np.random.Generator, count: int, depth: float, max_cycles: int = 6) -> np.ndarray:
    lfo = np.zeros(N)
    for _ in range(count):
        c = int(rng.integers(1, max_cycles + 1))
        lfo += rng.uniform(0.4, 1.0) * np.sin(2 * np.pi * c * T / SECONDS + rng.uniform(0, 6.28))
    lfo /= max(1e-9, np.max(np.abs(lfo)))
    return 1.0 + depth * lfo


def _pluck(freq: float, seconds: float, rng: np.random.Generator) -> np.ndarray:
    n = int(seconds * MUSIC_SR)
    t = np.arange(n) / MUSIC_SR
    out = np.zeros(n)
    for k, a in enumerate((1.0, 0.5, 0.33, 0.2, 0.12, 0.07), start=1):
        if freq * k > MUSIC_SR * 0.45:
            break
        out += a * np.sin(2 * np.pi * freq * k * (1 + rng.uniform(-0.0008, 0.0008)) * t + rng.uniform(0, 6.28)) \
            * np.exp(-t / (seconds * 0.28 / (1 + 0.5 * k)))
    out *= np.minimum(1.0, t / 0.004)
    out *= np.clip((seconds - t) / 0.3, 0, 1)
    return lowpass(out, 2400, 2, MUSIC_SR)


def _stereo(left: np.ndarray, right: np.ndarray, peak: float) -> np.ndarray:
    data = np.stack([left, right], axis=1)
    data -= np.mean(data, axis=0)
    return data / np.max(np.abs(data)) * peak


def world_music() -> np.ndarray:
    rng = rng_for("music_world")
    left = np.zeros(N)
    right = np.zeros(N)
    # Drone: A1 and E2 with detuned twin partials; each side leans a little different for width.
    for f, a in ((55.0, 1.0), (82.5, 0.6), (110.0, 0.45), (164.7, 0.2), (220.0, 0.12)):
        for side, det in ((0, -0.15), (1, 0.15)):
            fr = _snap(f + det)
            part = a * np.sin(2 * np.pi * fr * T + rng.uniform(0, 6.28)) * _periodic_lfo(rng, 2, 0.3)
            (left if side == 0 else right)[:] += part * (0.65 if (side == 0) == (f < 100) else 0.5)
    # Dark pad: filtered noise swelling slowly.
    for buf in (left, right):
        pad = bandpass(noise(N, rng), 110, 520, 2, MUSIC_SR)
        buf += pad * 0.5 * _periodic_lfo(rng, 3, 0.7, 4)
    # Sparse minor-key plucks (A, C, E, F, G, low Bb colour), each wrapped so none ends in a cut.
    scale = [110.0, 130.8, 164.8, 174.6, 196.0, 220.0, 261.6, 329.6, 116.5]
    times = [1.2, 4.9, 7.1, 11.3, 14.6, 17.4]
    for i, start in enumerate(times):
        f = scale[int(rng.integers(0, len(scale)))] * (2 if i % 3 == 2 else 1)
        p = _pluck(f, 4.5, rng)
        pan = rng.uniform(0.2, 0.8)
        add_at(left, p * (1 - pan) * 0.9, int(start * MUSIC_SR), wrap=True)
        add_at(right, p * pan * 0.9, int(start * MUSIC_SR), wrap=True)
    ir_l = reverb_ir(2.6, rng, MUSIC_SR, 2200)
    ir_r = reverb_ir(2.6, rng, MUSIC_SR, 2200)
    left = circular_reverb(left, ir_l, 0.45)
    right = circular_reverb(right, ir_r, 0.45)
    return _stereo(left, right, 0.7)


def menu_music() -> np.ndarray:
    rng = rng_for("music_menu")
    left = np.zeros(N)
    right = np.zeros(N)
    seg = SECONDS / 4.0
    chords = [
        (73.4, 110.0, 146.8, 174.6),   # D minor
        (58.3, 116.5, 146.8, 174.6),   # Bb
        (98.0, 116.5, 146.8, 196.0),   # G minor
        (55.0, 110.0, 138.6, 164.8),   # A (major third: the pull back to D minor)
    ]
    drawbars = (1.0, 0.7, 0.55, 0.4, 0.3, 0.18, 0.1)
    formants = ((650, 110), (1080, 150), (2650, 250))
    for i, chord in enumerate(chords):
        centre = (i + 0.5) * seg
        d = (T - centre + SECONDS / 2) % SECONDS - SECONDS / 2   # signed distance, wrapped
        win = np.where(np.abs(d) < seg, np.cos(np.pi * d / (2 * seg)) ** 2, 0.0)
        for note_index, f in enumerate(chord):
            fr = _snap(f)
            # Organ: drawbar partials. Choir: slightly detuned saw-ish stack through vowel formants.
            organ = sum(a * np.sin(2 * np.pi * fr * (k + 1) * T) for k, a in enumerate(drawbars))
            choir = np.zeros(N)
            for det in (-0.25, 0.0, 0.3):
                f_c = _snap(f * 2 + det)
                for k in range(1, 18):
                    fk = f_c * k
                    if fk > 5000:
                        break
                    g = sum(np.exp(-0.5 * ((fk - c) / w) ** 2) for c, w in formants) + 0.02
                    choir += g / k * np.sin(2 * np.pi * fk * T + rng.uniform(0, 6.28))
            voice = (organ * 0.5 + choir * 0.9) * win
            weight = 1.0 if note_index == 0 else 0.7
            pan = 0.35 + 0.1 * note_index
            left += voice * weight * (1 - pan)
            right += voice * weight * pan
    # Soft sub tolling on every chord change, like a distant bell.
    for i in range(4):
        start = int(i * seg * MUSIC_SR)
        f = _snap(chords[i][0] * 0.5 * 4)
        n = int(3.5 * MUSIC_SR)
        t = np.arange(n) / MUSIC_SR
        toll = (np.sin(2 * np.pi * f * t) + 0.4 * np.sin(2 * np.pi * f * 2.41 * t)) * np.exp(-t / 1.2) * np.minimum(1, t / 0.01)
        add_at(left, toll * 0.5, start, wrap=True)
        add_at(right, toll * 0.5, start, wrap=True)
    ir_l = reverb_ir(3.4, rng, MUSIC_SR, 2600)
    ir_r = reverb_ir(3.4, rng, MUSIC_SR, 2600)
    left = circular_reverb(left, ir_l, 0.5)
    right = circular_reverb(right, ir_r, 0.5)
    return _stereo(left, right, 0.7)


def build(out_root: Path) -> list[Path]:
    music = out_root / "music"
    written = []
    for name, fn in (("world", world_music), ("menu", menu_music)):
        p = music / f"{name}.wav"
        write_wav(p, fn(), MUSIC_SR)
        written.append(p)
    return written
