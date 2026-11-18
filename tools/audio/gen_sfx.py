"""Legacy procedural one-shots for dream that the gothic pack leaves unchanged. Original synthesis only.

Run through build_all.py. Output: dream/audio/sfx/*.wav (44.1 kHz mono): block_place, loot_pickup,
potion_drink, level_up, respawn, stat_spend, ui_click, ui_hover, inventory_open, inventory_close.
Combat, movement and gear live in sfx_combat.py, wolves in sfx_wolf.py, loops in gen_music.py and
gen_ambience.py.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (SR, add_at, bandpass, bell, decay, finalize, lowpass, noise, rng_for, samples,
                   sine, sweep, tvec, write_wav)


# ---------------------------------------------------------------- world

def block_place() -> np.ndarray:
    rng = rng_for("block_place")
    n = samples(0.28)
    thunk = sine(sweep(160, 70, n, 6.0), n) * decay(n, 0.07)
    body = lowpass(noise(n, rng), 900) * decay(n, 0.04)
    click = bandpass(noise(n, rng), 1800, 5000) * decay(n, 0.006, attack=0.0003) * 0.4
    return finalize(thunk + body * 1.2 + click, 0.82)



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



# ---------------------------------------------------------------- vendor

def vendor_coin() -> np.ndarray:
    """Gold changing hands in a cold stone shop: a heavy metal clank, a short coin-on-stone tail."""
    rng = rng_for("vendor_coin")
    n = samples(0.42)
    # The clank: a low struck-metal body, inharmonic and quick to die.
    clank = bell(420, 0.30, rng, ratios=(1.0, 1.51, 2.32, 3.1), decays=(1.0, 0.6, 0.35, 0.2),
                 amps=(1.0, 0.55, 0.35, 0.18))
    out = np.zeros(n)
    add_at(out, clank, 0)
    out += sine(sweep(190, 95, n, 6.0), n) * decay(n, 0.05) * 0.8
    out += bandpass(noise(n, rng), 600, 3000) * decay(n, 0.012, attack=0.0006) * 0.35
    # The tail: a coin settling on stone, a dull ring and a smaller second knock.
    ln = samples(0.16)
    tt = tvec(ln)
    ring = (np.sin(2 * np.pi * 1450 * tt) + 0.4 * np.sin(2 * np.pi * 2130 * tt)) * np.exp(-tt / 0.035)
    add_at(out, ring * 0.22, samples(0.075))
    add_at(out, bandpass(noise(ln, rng), 500, 2200) * decay(ln, 0.01, attack=0.0006) * 0.25, samples(0.14))
    return finalize(out, 0.5, 0.0008, 0.06)


def vendor_refuse() -> np.ndarray:
    """A hand laid flat over the counter: a dull dead thud, no bright tail."""
    rng = rng_for("vendor_refuse")
    n = samples(0.26)
    thud = sine(sweep(120, 52, n, 7.0), n) * decay(n, 0.06, attack=0.001)
    body = lowpass(noise(n, rng), 420) * decay(n, 0.035, attack=0.001)
    return finalize(thud + body * 0.9, 0.5, 0.001, 0.05)


# ---------------------------------------------------------------- build

SINGLES = {
    "block_place": block_place, "loot_pickup": loot_pickup, "potion_drink": potion_drink,
    "level_up": level_up, "respawn": respawn, "stat_spend": stat_spend, "ui_click": ui_click,
    "ui_hover": ui_hover, "inventory_open": inventory_open, "inventory_close": inventory_close,
    "vendor_coin": vendor_coin, "vendor_refuse": vendor_refuse,
}


def build(out_root: Path) -> list[Path]:
    written: list[Path] = []
    sfx = out_root / "sfx"
    for name, fn in SINGLES.items():
        p = sfx / f"{name}.wav"
        write_wav(p, fn(), SR)
        written.append(p)
    return written
