"""Combat, movement and gear/world one-shots for dream (dark, grim, gothic). Original synthesis only.

Owned by the combat workstream. Run through build_all.py. Output: 19 files in dream/audio/sfx
(44.1 kHz mono): hit_1..3, hit_critical, swing_1..3, player_hurt, player_death, footstep_1..4,
land, jump, equip, unequip, block_break, block_crack.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (SR, add_at, bandpass, decay, finalize, formant_voice, highpass, lowpass, noise,
                   rng_for, samples, sine, sweep, tvec, write_wav)


# ---------------------------------------------------------------- movement

def footstep(variant: int) -> np.ndarray:
    rng = rng_for(f"footstep_{variant}")
    n = samples(0.3)
    base = 62 + 9 * variant
    thump = sine(sweep(base * 1.6, base * 0.7, n, 6.0), n) * decay(n, 0.07)
    body = lowpass(noise(n, rng), 380 + 40 * variant) * decay(n, 0.05)
    grit = bandpass(noise(n, rng), 1200, 3200) * decay(n, 0.012, attack=0.0005) * 0.35
    drag = bandpass(noise(n, rng), 200, 900) * decay(n, 0.11, attack=0.02) * 0.18
    return finalize(thump * 1.0 + body * 1.4 + grit + drag, 0.8)


def land() -> np.ndarray:
    rng = rng_for("land")
    n = samples(0.42)
    thump = sine(sweep(110, 38, n, 5.0), n) * decay(n, 0.12)
    body = lowpass(noise(n, rng), 450) * decay(n, 0.09)
    grit = bandpass(noise(n, rng), 800, 2800) * decay(n, 0.02) * 0.4
    return finalize(thump * 1.2 + body * 1.4 + grit, 0.85)


def jump() -> np.ndarray:
    rng = rng_for("jump")
    n = samples(0.28)
    t = tvec(n)
    air = bandpass(noise(n, rng), 250, 1400) * np.sin(np.pi * np.clip(t / 0.28, 0, 1)) ** 1.5
    cloth = highpass(noise(n, rng), 3000) * decay(n, 0.04, attack=0.01) * 0.1
    return finalize(air + cloth, 0.55, 0.01, 0.06)


def swing(variant: int) -> np.ndarray:
    rng = rng_for(f"swing_{variant}")
    dur = 0.34 + 0.04 * variant
    n = samples(dur)
    t = tvec(n)
    centre = np.linspace(500 + 120 * variant, 1900 - 150 * variant, n)
    out = np.zeros(n)
    # A moving band built from fixed bands with bell shaped envelopes at staggered times.
    bands = [(300, 700), (600, 1400), (1100, 2600), (2000, 4200)]
    base = noise(n, rng)
    for i, (lo, hi) in enumerate(bands):
        peak_t = dur * (0.25 + 0.17 * i)
        env = np.exp(-0.5 * ((t - peak_t) / (dur * 0.16)) ** 2)
        out += bandpass(base, lo, hi) * env * (1.0 - 0.15 * i)
    out *= np.minimum(1.0, t / 0.03)
    if variant == 2:
        # Metal blade: a faint inharmonic shimmer riding the whoosh.
        for r, a in ((1.0, 0.5), (2.76, 0.3), (5.4, 0.18)):
            out += a * 0.25 * np.sin(2 * np.pi * 2300 * r * t) * np.exp(-0.5 * ((t - dur * 0.45) / (dur * 0.2)) ** 2)
    del centre
    return finalize(out, 0.7, 0.01, 0.06)



# ---------------------------------------------------------------- combat

def hit_flesh(variant: int) -> np.ndarray:
    rng = rng_for(f"hit_{variant}")
    n = samples(0.34)
    thud = sine(sweep(130 - 10 * variant, 45, n, 7.0), n) * decay(n, 0.1)
    meat = lowpass(noise(n, rng), 700 + 60 * variant) * decay(n, 0.06)
    squelch = bandpass(noise(n, rng), 250, 900) * decay(n, 0.09, attack=0.01) * 0.5
    snap = bandpass(noise(n, rng), 1500, 4000) * decay(n, 0.008, attack=0.0003) * 0.45
    return finalize(thud * 1.1 + meat * 1.3 + squelch + snap, 0.88)


def hit_critical() -> np.ndarray:
    rng = rng_for("hit_critical")
    n = samples(0.55)
    t = tvec(n)
    thud = sine(sweep(150, 38, n, 7.0), n) * decay(n, 0.13)
    meat = lowpass(noise(n, rng), 800) * decay(n, 0.07)
    crunch = np.zeros(n)
    for start in rng.uniform(0.0, 0.07, 9):
        burst = highpass(noise(samples(0.02), rng), 1800) * decay(samples(0.02), 0.004, attack=0.0002)
        add_at(crunch, burst * rng.uniform(0.5, 1.0), int(start * SR))
    ring = sum(a * np.sin(2 * np.pi * 1180 * r * t) for r, a in ((1.0, 0.5), (2.32, 0.3), (4.1, 0.2))) * np.exp(-t / 0.12) * 0.22
    return finalize(thud * 1.1 + meat * 1.2 + crunch * 0.7 + ring, 0.92, 0.002, 0.04)


def _grunt(f0_start: float, f0_end: float, dur: float, name: str, formants, breath=0.25) -> np.ndarray:
    rng = rng_for(name)
    n = samples(dur)
    t = tvec(n)
    f0 = sweep(f0_start, f0_end, n, 2.0) * (1.0 + 0.012 * np.sin(2 * np.pi * 6.5 * t))
    voice = formant_voice(f0, formants, n, tilt=1.0)
    amp = np.minimum(1.0, t / 0.025) * np.exp(-t / (dur * 0.45))
    air = bandpass(noise(n, rng), 400, 2400) * amp * breath
    return voice * amp * 0.6 + air


def player_hurt() -> np.ndarray:
    voice = _grunt(165, 105, 0.34, "player_hurt", [(520, 110, 1.0), (1050, 160, 0.7), (2400, 300, 0.25)], 0.22)
    return finalize(voice, 0.85, 0.003, 0.05)


def player_death() -> np.ndarray:
    a = _grunt(150, 70, 0.9, "player_death", [(450, 110, 1.0), (900, 150, 0.7), (2300, 300, 0.2)], 0.2)
    rng = rng_for("player_death_fall")
    n = len(a)
    fall = lowpass(noise(n, rng), 300) * decay(n, 0.1, attack=0.002) * 0.0
    tail_start = samples(0.55)
    thud_n = samples(0.4)
    thud = sine(sweep(90, 35, thud_n, 5.0), thud_n) * decay(thud_n, 0.1) + lowpass(noise(thud_n, rng), 300) * decay(thud_n, 0.08)
    full = np.zeros(n + samples(0.3))
    full[:n] += a
    full[:n] += fall
    add_at(full, thud * 0.9, tail_start)
    return finalize(full, 0.88, 0.003, 0.15)



# ---------------------------------------------------------------- gear and world

def equip() -> np.ndarray:
    rng = rng_for("equip")
    n = samples(0.5)
    t = tvec(n)
    metal = sum(a * np.sin(2 * np.pi * 1450 * r * t + rng.uniform(0, 6)) * np.exp(-t / (0.2 / r ** 0.5))
                for r, a in ((1.0, 1.0), (2.76, 0.6), (5.4, 0.35), (8.93, 0.2)))
    leather = lowpass(noise(n, rng), 600) * decay(n, 0.04) * 1.2
    return finalize(metal * 0.5 + leather, 0.75, 0.001, 0.08)


def unequip() -> np.ndarray:
    rng = rng_for("unequip")
    n = samples(0.4)
    t = tvec(n)
    metal = sum(a * np.sin(2 * np.pi * 980 * r * t + rng.uniform(0, 6)) * np.exp(-t / (0.12 / r ** 0.5))
                for r, a in ((1.0, 1.0), (2.76, 0.5), (5.4, 0.25)))
    leather = lowpass(noise(n, rng), 450) * decay(n, 0.05) * 1.1
    return finalize(metal * 0.3 + leather, 0.65, 0.001, 0.08)



def block_break() -> np.ndarray:
    rng = rng_for("block_break")
    n = samples(0.9)
    out = np.zeros(n)
    rumble = lowpass(noise(n, rng), 220) * decay(n, 0.25, attack=0.01) * 1.3
    out += rumble
    # Crumble: many small gritty fragments, dense early and sparse late.
    count = 90
    starts = (rng.random(count) ** 1.8) * 0.7
    for s in starts:
        ln = samples(rng.uniform(0.008, 0.03))
        lo = rng.uniform(300, 1500)
        frag = bandpass(noise(ln, rng), lo, lo * rng.uniform(2.0, 3.5)) * decay(ln, ln / SR / 3.0, attack=0.0003)
        add_at(out, frag * rng.uniform(0.2, 1.0) * (1.2 - s), int(s * SR))
    thud = sine(sweep(120, 45, n, 6.0), n) * decay(n, 0.1)
    return finalize(out * 0.7 + thud, 0.88, 0.002, 0.15)


def block_crack() -> np.ndarray:
    rng = rng_for("block_crack")
    n = samples(0.36)
    out = np.zeros(n)
    for s, a in ((0.0, 1.0), (0.045, 0.7), (0.11, 0.45), (0.19, 0.3)):
        ln = samples(0.04)
        tick = bandpass(noise(ln, rng), 900, 4500) * decay(ln, 0.007, attack=0.0002)
        add_at(out, tick * a, int(s * SR))
    out += lowpass(noise(n, rng), 300) * decay(n, 0.05) * 0.5
    return finalize(out, 0.7, 0.001, 0.08)



# ---------------------------------------------------------------- build

VARIANTS = {
    "footstep": (footstep, 4),
    "swing": (swing, 3),
    "hit": (hit_flesh, 3),
}

SINGLES = {
    "hit_critical": hit_critical, "player_hurt": player_hurt, "player_death": player_death,
    "land": land, "jump": jump, "equip": equip, "unequip": unequip,
    "block_break": block_break, "block_crack": block_crack,
}


def build(out_root: Path) -> list[Path]:
    written: list[Path] = []
    sfx = out_root / "sfx"
    for name, (fn, count) in VARIANTS.items():
        for v in range(1, count + 1):
            p = sfx / f"{name}_{v}.wav"
            write_wav(p, fn(v), SR)
            written.append(p)
    for name, fn in SINGLES.items():
        p = sfx / f"{name}.wav"
        write_wav(p, fn(), SR)
        written.append(p)
    return written
