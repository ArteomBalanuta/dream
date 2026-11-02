"""Procedural sound effects and ambience loops for dream (dark, grim, gothic). Original synthesis only.

Run through build_all.py. Output: dream/audio/sfx/*.wav (44.1 kHz mono) and dream/audio/ambience/*.wav
(22.05 kHz mono, seamless loops). A sound with variants is written as name_1.wav, name_2.wav, ...
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (SR, add_at, bandpass, bell, circular_reverb, decay, finalize, formant_voice,
                   highpass, lowpass, noise, reverb_ir, rng_for, samples, sine, sweep, tvec, write_wav)

AMB_SR = 22050


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


def wolf_hurt() -> np.ndarray:
    n = samples(0.38)
    t = tvec(n)
    f0 = np.interp(t, [0, 0.07, 0.38], [520, 980, 640]) * (1 + 0.02 * np.sin(2 * np.pi * 14 * t))
    voice = formant_voice(f0, [(900, 220, 1.0), (1800, 300, 0.6), (3000, 400, 0.2)], n, tilt=0.9)
    amp = np.minimum(1, t / 0.01) * np.exp(-t / 0.2)
    return finalize(voice * amp, 0.85, 0.003, 0.06)


def wolf_death() -> np.ndarray:
    n = samples(1.0)
    t = tvec(n)
    f0 = np.interp(t, [0, 0.08, 0.3, 1.0], [560, 1000, 620, 170]) * (1 + 0.025 * np.sin(2 * np.pi * 11 * t))
    voice = formant_voice(f0, [(850, 230, 1.0), (1700, 300, 0.55), (2900, 400, 0.2)], n, tilt=0.9)
    amp = np.minimum(1, t / 0.01) * np.exp(-t / 0.42)
    rng = rng_for("wolf_death")
    breath = bandpass(noise(n, rng), 600, 3000) * amp * 0.12
    return finalize(voice * amp + breath, 0.85, 0.003, 0.2)


def wolf_growl() -> np.ndarray:
    rng = rng_for("wolf_growl")
    n = samples(1.2)
    t = tvec(n)
    f0 = 62 + 8 * np.sin(2 * np.pi * 1.7 * t) + 6 * np.sin(2 * np.pi * 0.6 * t)
    voice = formant_voice(f0, [(380, 90, 1.0), (800, 160, 0.8), (1500, 250, 0.35)], n, harmonics=40, tilt=0.7)
    rasp = 0.55 + 0.45 * np.sign(np.sin(2 * np.pi * 27 * t)) * 0.5 + 0.2 * np.sin(2 * np.pi * 27 * t)
    swell = np.sin(np.pi * np.clip(t / 1.2, 0, 1)) ** 0.7
    air = bandpass(noise(n, rng), 300, 1100) * 0.35 * (0.5 + 0.5 * rasp)
    return finalize((voice * rasp + air) * swell, 0.8, 0.04, 0.2)


def wolf_howl() -> np.ndarray:
    rng = rng_for("wolf_howl")
    dur = 3.4
    n = samples(dur)
    t = tvec(n)
    f0 = np.interp(t, [0, 0.5, 1.6, 2.6, 3.4], [260, 430, 500, 470, 300])
    f0 = f0 * (1 + 0.012 * np.sin(2 * np.pi * 5.2 * t) * np.clip(t / 1.0, 0, 1))
    voice = formant_voice(f0, [(500, 140, 1.0), (1000, 200, 0.55), (2200, 300, 0.15)], n, tilt=1.3)
    amp = np.clip(t / 0.6, 0, 1) ** 1.5 * np.clip((dur - t) / 1.2, 0, 1) ** 1.2
    dry = voice * amp
    breath = bandpass(noise(n, rng), 500, 2500) * amp * 0.05
    sig = dry + breath
    # Distant: a dark echo tail and a low-passed, quiet body.
    ir = reverb_ir(1.6, rng, damp=1800)
    wet = np.convolve(sig, ir)[: n + len(ir)]
    full = np.zeros(len(wet))
    full[:n] += sig * 0.55
    full += wet * 0.8
    return finalize(lowpass(full, 3500, 1), 0.7, 0.05, 0.4)


# ---------------------------------------------------------------- world

def block_place() -> np.ndarray:
    rng = rng_for("block_place")
    n = samples(0.28)
    thunk = sine(sweep(160, 70, n, 6.0), n) * decay(n, 0.07)
    body = lowpass(noise(n, rng), 900) * decay(n, 0.04)
    click = bandpass(noise(n, rng), 1800, 5000) * decay(n, 0.006, attack=0.0003) * 0.4
    return finalize(thunk + body * 1.2 + click, 0.82)


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


# ---------------------------------------------------------------- items

def loot_pickup() -> np.ndarray:
    rng = rng_for("loot_pickup")
    n = samples(0.75)
    out = np.zeros(n)
    base = noise(n, rng)
    rustle = bandpass(base, 1800, 6000)
    gate = np.zeros(n)
    for s in np.sort(rng.uniform(0.0, 0.35, 14)):
        ln = samples(rng.uniform(0.01, 0.035))
        add_at(gate, np.hanning(ln) * rng.uniform(0.3, 1.0), int(s * SR))
    out += rustle * gate * 0.5
    out += lowpass(base, 500) * decay(n, 0.12, attack=0.01) * 0.4
    t = tvec(n)
    for s, f in ((0.12, 2350), (0.22, 3120), (0.31, 2700)):
        ln = samples(0.3)
        tt = tvec(ln)
        ping = (np.sin(2 * np.pi * f * tt) + 0.4 * np.sin(2 * np.pi * f * 2.76 * tt)) * np.exp(-tt / 0.06)
        add_at(out, ping * 0.35, int(s * SR))
    del t
    return finalize(out, 0.75, 0.002, 0.08)


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


def potion_drink() -> np.ndarray:
    rng = rng_for("potion_drink")
    n = samples(1.3)
    out = np.zeros(n)
    for s, f in ((0.0, 190), (0.14, 230), (0.31, 270)):
        ln = samples(0.12)
        tt = tvec(ln)
        glug = np.sin(2 * np.pi * np.cumsum(np.linspace(f, f * 2.0, ln)) / SR) * np.sin(np.pi * tt / 0.12) ** 1.2
        glug += lowpass(noise(ln, rng), 500) * np.sin(np.pi * tt / 0.12) * 0.25
        add_at(out, glug * 0.8, int(s * SR))
    t = tvec(n)
    shimmer = np.zeros(n)
    for f in (1318, 1760, 2349, 3136):
        shimmer += np.sin(2 * np.pi * f * t + rng.uniform(0, 6)) * (0.6 + 0.4 * np.sin(2 * np.pi * rng.uniform(5, 9) * t))
    shimmer *= np.clip((t - 0.4) / 0.25, 0, 1) * np.exp(-(t - 0.4).clip(0) / 0.35) * 0.1
    return finalize(out + shimmer, 0.75, 0.002, 0.15)


def level_up() -> np.ndarray:
    rng = rng_for("level_up")
    out = np.zeros(samples(4.2))
    for start, freq, gain in ((0.0, 164.8, 1.0), (0.55, 196.0, 0.8), (1.1, 246.9, 0.7)):
        b = bell(freq, 3.0, rng)
        t = tvec(len(b))
        swell = np.clip(t / 0.35, 0, 1) ** 2
        add_at(out, b * swell * gain, samples(start))
    pad = np.zeros(len(out))
    tp = tvec(len(out))
    for f in (82.4, 123.5, 164.8):
        pad += np.sin(2 * np.pi * f * tp)
    pad *= np.clip(tp / 1.2, 0, 1) ** 2 * np.exp(-(tp - 1.2).clip(0) / 1.2) * 0.12
    return finalize(out + pad, 0.8, 0.01, 0.5)


def respawn() -> np.ndarray:
    rng = rng_for("respawn")
    n = samples(1.8)
    t = tvec(n)
    breath = bandpass(noise(n, rng), 120, 700) * np.sin(np.pi * np.clip(t / 1.6, 0, 1)) ** 2
    pad = sum(np.sin(2 * np.pi * f * t) for f in (73.4, 110.0, 146.8)) * np.sin(np.pi * np.clip(t / 1.8, 0, 1)) ** 2 * 0.2
    b = bell(293.7, 1.5, rng)
    out = breath * 0.8 + pad
    add_at(out, b * 0.35, samples(0.5))
    return finalize(out, 0.7, 0.01, 0.3)


def stat_spend() -> np.ndarray:
    rng = rng_for("stat_spend")
    n = samples(0.14)
    t = tvec(n)
    tick = np.sin(2 * np.pi * 1900 * t) * np.exp(-t / 0.012)
    low = np.sin(2 * np.pi * 240 * t) * np.exp(-t / 0.02)
    grain = bandpass(noise(n, rng), 1500, 5000) * decay(n, 0.005, attack=0.0002)
    return finalize(tick * 0.6 + low * 0.7 + grain * 0.4, 0.55, 0.0005, 0.03)


# ---------------------------------------------------------------- ui

def ui_click() -> np.ndarray:
    rng = rng_for("ui_click")
    n = samples(0.12)
    t = tvec(n)
    thock = np.sin(2 * np.pi * np.cumsum(np.linspace(260, 150, n)) / SR) * np.exp(-t / 0.025)
    paper = bandpass(noise(n, rng), 1200, 4500) * decay(n, 0.008, attack=0.0003)
    return finalize(thock * 0.9 + paper * 0.5, 0.6, 0.0005, 0.03)


def ui_hover() -> np.ndarray:
    rng = rng_for("ui_hover")
    n = samples(0.07)
    tick = bandpass(noise(n, rng), 1800, 5500) * decay(n, 0.01, attack=0.001)
    low = lowpass(noise(n, rng), 400) * decay(n, 0.015) * 0.5
    return finalize(tick + low, 0.4, 0.001, 0.02)


def inventory_open() -> np.ndarray:
    rng = rng_for("inventory_open")
    n = samples(0.42)
    t = tvec(n)
    base = noise(n, rng)
    swish = bandpass(base, 300, 2200) * np.sin(np.pi * np.clip(t / 0.3, 0, 1)) ** 1.5
    creak = sine(sweep(210, 340, n, 2.0), n) * (1 + sine(31, n)) * np.exp(-0.5 * ((t - 0.15) / 0.07) ** 2) * 0.1
    buckle = bandpass(noise(n, rng), 2500, 6000) * decay(n, 0.01) * 0.25
    out = swish + creak
    add_at(out, buckle, samples(0.02))
    return finalize(out, 0.6, 0.004, 0.08)


def inventory_close() -> np.ndarray:
    rng = rng_for("inventory_close")
    n = samples(0.4)
    t = tvec(n)
    swish = bandpass(noise(n, rng), 250, 1800) * np.exp(-0.5 * ((t - 0.08) / 0.07) ** 2)
    thump = sine(sweep(130, 70, n, 6.0), n) * decay(n, 0.05) * 0.0
    flop = lowpass(noise(n, rng), 450) * decay(n, 0.05, attack=0.003)
    out = swish * 0.8
    add_at(out, flop * 1.0, samples(0.14))
    out += thump
    return finalize(out, 0.6, 0.004, 0.08)


# ---------------------------------------------------------------- ambience loops (22.05 kHz, periodic)

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


# ---------------------------------------------------------------- build

ONE_SHOTS = {
    "footstep": (lambda v: footstep(v), 4),
    "swing": (lambda v: swing(v), 3),
    "hit": (lambda v: hit_flesh(v), 3),
}

SINGLES = {
    "land": land, "jump": jump, "hit_critical": hit_critical, "player_hurt": player_hurt,
    "player_death": player_death, "wolf_hurt": wolf_hurt, "wolf_death": wolf_death, "wolf_growl": wolf_growl,
    "wolf_howl": wolf_howl, "block_place": block_place, "block_break": block_break, "block_crack": block_crack,
    "loot_pickup": loot_pickup, "equip": equip, "unequip": unequip, "potion_drink": potion_drink,
    "level_up": level_up, "respawn": respawn, "stat_spend": stat_spend, "ui_click": ui_click,
    "ui_hover": ui_hover, "inventory_open": inventory_open, "inventory_close": inventory_close,
}


def build(out_root: Path) -> list[Path]:
    written: list[Path] = []
    sfx = out_root / "sfx"
    for name, (fn, count) in ONE_SHOTS.items():
        for v in range(1, count + 1):
            p = sfx / f"{name}_{v}.wav"
            write_wav(p, fn(v), SR)
            written.append(p)
    for name, fn in SINGLES.items():
        p = sfx / f"{name}.wav"
        write_wav(p, fn(), SR)
        written.append(p)
    amb = out_root / "ambience"
    for name, fn in (("wind", wind_loop), ("night", night_loop)):
        p = amb / f"{name}.wav"
        write_wav(p, fn(), AMB_SR)
        written.append(p)
    return written
