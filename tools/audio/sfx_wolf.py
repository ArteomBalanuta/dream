"""Wolf voice one-shots for dream (dark, grim, gothic). Original synthesis only.

Owned by the wolf workstream. Run through build_all.py. Output: 7 files in dream/audio/sfx
(44.1 kHz mono): wolf_howl_1..3, wolf_growl_1..2, wolf_hurt, wolf_death.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (SR, bandpass, finalize, formant_voice, lowpass, noise, reverb_ir, rng_for,
                   samples, tvec, write_wav)


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



# ---------------------------------------------------------------- build

# Placeholder until the wolf pack lands: the howl and growl variants reuse the one legacy voice.
VARIANTS = {
    "wolf_howl": (lambda v: wolf_howl(), 3),
    "wolf_growl": (lambda v: wolf_growl(), 2),
}

SINGLES = {"wolf_hurt": wolf_hurt, "wolf_death": wolf_death}


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
