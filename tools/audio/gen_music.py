"""Two seamless music loops for dream, 22.05 kHz stereo, 24 s each. Original synthesis only.

Menu: a gothic cathedral at night. A pipe organ (principal 8' + 4' on the manual, a 16' pedal,
chiff on every onset), three low monks on "oo" and "ah" (glottal-style harmonics through bass
formants, per-singer detune, irregular vibrato) and a distant bronze bell tolling with real bell
partials. D minor, four slow bars: Dm, Bb, Gm, A. Notes have attacks, sustains and releases on a
timeline (each voice moves on its own beat), and the room is a long circular reverb.
World: the exploration bed. Low bowed-string drones (E phrygian, a semitone clash that resolves),
sparse dark lute plucks and one very distant bell.
Everything is built from events that start and end at zero and are wrapped with add_at(wrap=True),
the reverb is circular, so the end meets the start with no click.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (add_at, bandpass, circular_reverb, env_adsr, formant_voice, jitter, lowpass,
                   room_ir, rng_for, write_wav)

MUSIC_SR = 22050
SECONDS = 24.0
N = int(SECONDS * MUSIC_SR)


def _at(sec: float) -> int:
    return int(round(sec * MUSIC_SR))


def _slow_noise(n: int, rng: np.random.Generator, cutoff: float) -> np.ndarray:
    y = lowpass(rng.standard_normal(n), cutoff, 2, MUSIC_SR)
    return y / (np.sqrt(np.mean(y ** 2)) or 1.0)


class _Mix:
    """Stereo accumulator: place a mono event with a pan (0 left .. 1 right), wrapped around the loop."""

    def __init__(self) -> None:
        self.left = np.zeros(N)
        self.right = np.zeros(N)

    def put(self, sig: np.ndarray, start_s: float, pan: float, gain: float = 1.0) -> None:
        a = np.cos(pan * np.pi / 2)
        b = np.sin(pan * np.pi / 2)
        add_at(self.left, sig * gain * a, _at(start_s), wrap=True)
        add_at(self.right, sig * gain * b, _at(start_s), wrap=True)

    def finish(self, rng: np.random.Generator, seconds: float, wet: float, damp: float,
               peak: float = 0.7) -> np.ndarray:
        outs = []
        for ch in (self.left, self.right):
            ir = room_ir(seconds, rng, MUSIC_SR, damp, predelay=0.035)
            outs.append(circular_reverb(ch, ir, wet))
        data = np.stack(outs, axis=1)
        data -= np.mean(data, axis=0)
        return data / np.max(np.abs(data)) * peak


# ---------------------------------------------------------------- instruments

def _organ_note(freq: float, dur: float, rng: np.random.Generator, pedal: bool = False) -> np.ndarray:
    """Principal 8' plus a quieter 4' (or a 16' flue pedal), with a chiff and a clean release."""
    release = 0.55
    n = _at(dur + release)
    t = np.arange(n) / MUSIC_SR
    out = np.zeros(n)
    if pedal:
        stops = ((1.0, (1.0, 0.65, 0.35, 0.18, 0.08)), (0.25, (1.0, 0.4, 0.1)))
        ratios = (1.0, 0.5)
    else:
        stops = ((1.0, (1.0, 0.55, 0.32, 0.2, 0.11, 0.07, 0.04, 0.02)), (0.45, (1.0, 0.5, 0.25, 0.1, 0.05)), (0.16, (1.0, 0.5, 0.25)))
        ratios = (1.0, 2.0, 4.0)
    for (level, weights), ratio in zip(stops, ratios):
        f = freq * ratio * (1.0 + rng.uniform(-0.0012, 0.0012))
        for k, w in enumerate(weights, start=1):
            if f * k > MUSIC_SR * 0.45:
                break
            out += level * w * np.sin(2 * np.pi * f * k * t + rng.uniform(0, 6.28))
    # A real wind supply is not perfectly steady: tiny irregular level drift.
    out *= 1.0 + 0.02 * _slow_noise(n, rng, 1.5)
    out *= env_adsr(n, 0.045, 0.12, 0.88, release, MUSIC_SR)
    # Chiff: the breathy speech of the pipe as it starts, around its upper partials.
    cn = _at(0.06)
    chiff = bandpass(rng.standard_normal(cn), min(freq * 3.0, 4500.0), min(freq * 7.0, 5000.0), 2, MUSIC_SR)
    chiff *= np.exp(-np.arange(cn) / MUSIC_SR / 0.012) * np.minimum(1.0, np.arange(cn) / (0.004 * MUSIC_SR))
    out[:cn] += chiff / (np.max(np.abs(chiff)) or 1.0) * 0.28
    return out


_VOWELS = {
    # centre Hz, width Hz, amplitude: a low male voice. The widths keep single harmonics audible.
    "oo": ((310.0, 55.0, 1.0), (820.0, 90.0, 0.35), (2400.0, 200.0, 0.05)),
    "ah": ((620.0, 75.0, 1.0), (1050.0, 100.0, 0.55), (2500.0, 220.0, 0.10)),
}


def _monk(freq: float, dur: float, vowel: str, rng: np.random.Generator) -> np.ndarray:
    """One chanting voice: glottal-ish harmonics through bass formants, irregular vibrato, breath."""
    attack, release = 0.75, 0.95
    n = _at(dur + release)
    env = env_adsr(n, attack, 0.4, 0.85, release, MUSIC_SR)
    out = np.zeros(n)
    # Two singers per line, each with its own tuning, vibrato, jitter and shimmer.
    for cents in (-7.0, 6.0):
        vib_depth = rng.uniform(0.0025, 0.0045) * np.minimum(1.0, np.arange(n) / (1.5 * MUSIC_SR))
        vib = np.sin(2 * np.pi * np.cumsum(rng.uniform(4.6, 5.8) * jitter(n, rng, 0.2, 1.5, MUSIC_SR)) / MUSIC_SR
                     + rng.uniform(0, 6.28))
        f0 = freq * 2 ** (cents / 1200.0) * (1.0 + vib_depth * vib) * jitter(n, rng, 0.0025, 40.0, MUSIC_SR)
        # Drift of the pitch a little off the written note, a human cannot hold it dead still.
        f0 *= 1.0 + 0.003 * _slow_noise(n, rng, 0.6)
        voice = formant_voice(f0, list(_VOWELS[vowel]), n, harmonics=34, tilt=1.35, sr=MUSIC_SR)
        voice *= jitter(n, rng, 0.07, 11.0, MUSIC_SR)
        breath = bandpass(rng.standard_normal(n), 400.0, 3200.0, 1, MUSIC_SR)
        voice += 0.05 * (np.max(np.abs(voice)) or 1.0) * breath / (np.max(np.abs(breath)) or 1.0)
        out += voice
    return out * env / (np.max(np.abs(out)) or 1.0)


def _bell(prime: float, seconds: float, rng: np.random.Generator) -> np.ndarray:
    """Bronze church bell: hum, prime, minor third, fifth, nominal and higher partials, each pair
    slightly split so the tone beats, short strike tick, upper partials dying first."""
    n = _at(seconds)
    t = np.arange(n) / MUSIC_SR
    # ratio, decay seconds, amplitude
    partials = ((0.5, 9.0, 0.75), (1.0, 7.0, 1.0), (1.2, 5.0, 0.6), (1.5, 4.0, 0.42), (2.0, 4.2, 0.55),
                (2.5, 2.2, 0.2), (3.0, 1.9, 0.22), (4.0, 1.4, 0.16), (5.33, 0.9, 0.08), (6.7, 0.6, 0.05))
    out = np.zeros(n)
    for ratio, decay, amp in partials:
        f = prime * ratio
        if f > MUSIC_SR * 0.45:
            continue
        split = rng.uniform(0.6, 2.4) if ratio < 3 else rng.uniform(2.0, 5.0)
        for fx in (f, f + split):
            out += amp * 0.5 * np.sin(2 * np.pi * fx * t + rng.uniform(0, 6.28)) * np.exp(-t / decay)
    out *= np.minimum(1.0, t / 0.004)
    tick = _at(0.05)
    strike = bandpass(rng.standard_normal(tick), 900.0, 4000.0, 2, MUSIC_SR) * np.exp(-np.arange(tick) / MUSIC_SR / 0.008)
    out[:tick] += strike / (np.max(np.abs(strike)) or 1.0) * 0.35
    out *= np.minimum(1.0, (n - np.arange(n)) / (0.5 * MUSIC_SR))
    return out


def _lute(freq: float, rng: np.random.Generator, seconds: float = 3.6) -> np.ndarray:
    """Plucked string: stiff slightly sharp partials, a pluck-position comb, highs dying fast."""
    n = _at(seconds)
    t = np.arange(n) / MUSIC_SR
    out = np.zeros(n)
    pos = rng.uniform(0.12, 0.2)
    for k in range(1, 18):
        f = freq * k * np.sqrt(1.0 + 0.00012 * k * k)
        if f > MUSIC_SR * 0.42:
            break
        amp = abs(np.sin(np.pi * k * pos)) / k ** 0.9
        out += amp * np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28)) * np.exp(-t / (1.7 / (1.0 + 0.55 * (k - 1))))
    out *= np.minimum(1.0, t / 0.002)
    tick = _at(0.012)
    out[:tick] += lowpass(rng.standard_normal(tick), 3000.0, 1, MUSIC_SR) * 0.05 * np.exp(-np.arange(tick) / MUSIC_SR / 0.003)
    out = lowpass(out, 2600.0, 2, MUSIC_SR)
    # The wooden body: a soft resonance near 200 Hz and another near 420 Hz.
    body = bandpass(out, 160.0, 260.0, 2, MUSIC_SR) * 0.5 + bandpass(out, 360.0, 480.0, 2, MUSIC_SR) * 0.25
    out = out + body
    out *= np.minimum(1.0, (n - np.arange(n)) / (0.3 * MUSIC_SR))
    return out


def _bowed(freq: float, dur: float, rng: np.random.Generator, release: float = 2.2, attack: float = 1.4) -> np.ndarray:
    """Cello or viola da gamba drone: band-limited saw, body resonances, bow noise, pitch wander."""
    n = _at(dur + release)
    t = np.arange(n) / MUSIC_SR
    f0 = freq * (1.0 + 0.0022 * _slow_noise(n, rng, 0.9)) * (1.0 + 0.0012 * np.sin(2 * np.pi * rng.uniform(4.2, 5.0) * t) * np.minimum(1.0, t / 3.0))
    phase = 2 * np.pi * np.cumsum(f0) / MUSIC_SR
    out = np.zeros(n)
    for k in range(1, 40):
        fk = freq * k
        if fk > 2600.0:
            break
        body = 1.0 + 1.6 * np.exp(-0.5 * ((fk - 230.0) / 60.0) ** 2) + 1.0 * np.exp(-0.5 * ((fk - 520.0) / 110.0) ** 2) \
            + 0.8 * np.exp(-0.5 * ((fk - 1050.0) / 250.0) ** 2)
        out += body / k ** 1.05 * np.sin(k * phase + rng.uniform(0, 0.5))
    pressure = 1.0 + 0.12 * _slow_noise(n, rng, 3.0)
    scrape = bandpass(rng.standard_normal(n), 900.0, 3500.0, 1, MUSIC_SR)
    out = out * pressure + 0.45 * scrape / (np.max(np.abs(scrape)) or 1.0) * np.max(np.abs(out)) * 0.05 * pressure
    return lowpass(out, 3000.0, 1, MUSIC_SR) * env_adsr(n, attack, 0.8, 0.8, release, MUSIC_SR) / (np.max(np.abs(out)) or 1.0)


# ---------------------------------------------------------------- loops

def menu_music() -> np.ndarray:
    rng = rng_for("music_menu")
    mix = _Mix()
    bar = SECONDS / 4.0
    # (pedal, manual chord) per bar: Dm, Bb, Gm, A with the raised third pulling back to D minor.
    organ = (
        (73.42, (146.83, 174.61, 220.0)),
        (58.27, (146.83, 174.61, 233.08)),
        (49.0, (146.83, 196.0, 233.08)),
        (55.0, (164.81, 220.0, 277.18)),
    )
    # (bass, baritone, tenor) monks per bar, then the vowel each bar.
    choir = (
        (73.42, 87.31, 110.0),
        (73.42, 87.31, 116.54),
        (98.0, 116.54, 146.83),
        (110.0, 138.59, 164.81),
    )
    vowels = ("oo", "ah", "oo", "ah")
    for i in range(4):
        t0 = i * bar
        pedal, chord = organ[i]
        mix.put(_organ_note(pedal, bar - 0.9, rng, pedal=True), t0 + 0.02, 0.5, 0.55)
        for j, f in enumerate(chord):
            roll = 0.04 * j + rng.uniform(0.0, 0.03)   # the hands do not land together
            mix.put(_organ_note(f, bar - 1.1 - 0.08 * j, rng), t0 + 0.05 + roll, 0.38 + 0.12 * j, 0.7)
        # The monks come in after the organ, each on their own breath.
        for j, f in enumerate(choir[i]):
            start = t0 + 0.7 + 0.35 * j + rng.uniform(0.0, 0.25)
            mix.put(_monk(f, bar - 1.6 - 0.2 * j, vowels[(i + j) % 2] if j == 1 else vowels[i], rng), start, 0.3 + 0.2 * j, 0.85)
    # The bell, far off, tolling at the middle of each bar.
    for i, level in enumerate((1.0, 0.8, 0.92, 0.7)):
        strike = _bell(293.66, 14.0, rng)
        strike = lowpass(strike, 3800.0, 2, MUSIC_SR)
        mix.put(strike, i * bar + 3.0, 0.62, 1.7 * level)
    return mix.finish(rng, 5.5, 0.42, 3000.0)


def world_music() -> np.ndarray:
    rng = rng_for("music_world")
    mix = _Mix()
    # Bowed drones in E phrygian: E held, a fifth above it, then the F a semitone over it.
    mix.put(_bowed(82.41, 13.5, rng), 0.0, 0.42, 0.9)
    mix.put(_bowed(123.47, 8.0, rng), 3.5, 0.6, 0.45)
    mix.put(_bowed(87.31, 6.5, rng), 11.0, 0.5, 0.5)
    mix.put(_bowed(82.41, 7.0, rng), 16.5, 0.45, 0.85)
    mix.put(_bowed(196.0, 4.0, rng, release=2.5, attack=1.8), 18.5, 0.7, 0.18)
    # Sparse lute notes, a little late or early, never on a grid.
    for when, f, g in ((2.2, 329.63, 0.75), (3.45, 246.94, 0.6), (8.8, 196.0, 0.7), (9.85, 174.61, 0.55),
                       (15.3, 246.94, 0.65), (16.5, 261.63, 0.5), (21.1, 164.81, 0.8)):
        mix.put(_lute(f, rng), when + rng.uniform(-0.05, 0.05), rng.uniform(0.3, 0.7), g * 0.45)
    # One very distant bell.
    bell = lowpass(_bell(164.81, 12.0, rng), 1800.0, 2, MUSIC_SR)
    mix.put(bell, 12.5, 0.56, 0.14)
    return mix.finish(rng, 3.2, 0.38, 2600.0, 0.62)


def build(out_root: Path) -> list[Path]:
    music = out_root / "music"
    written = []
    for name, fn in (("world", world_music), ("menu", menu_music)):
        p = music / f"{name}.wav"
        write_wav(p, fn(), MUSIC_SR)
        written.append(p)
    return written
