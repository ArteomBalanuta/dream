"""Combat, movement and gear/world one-shots for dream (dark, grim, gothic). Original synthesis only.

Owned by the combat workstream. Run through build_all.py. Output: 19 files in dream/audio/sfx
(44.1 kHz mono): hit_1..3, hit_critical, swing_1..3, player_hurt, player_death, footstep_1..4,
land, jump, equip, unequip, block_break, block_crack.

Every sound is layered from short transients with millisecond offsets, damped modal resonances for
materials, granular micro-events for grit, chainmail and debris, a glottal pulse train for the
voice, and a small outdoor reflection. Nothing is a bare sine sweep.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (SR, add_at, bandpass, brown, decay, finalize, fir_convolve, highpass, jitter,
                   lowpass, modal, noise, onepole_lowpass_tv, pink, resonator_tv, rng_for,
                   room_ir, samples, soft_clip, tvec, write_wav)


# ---------------------------------------------------------------- private helpers

def _unit(x: np.ndarray) -> np.ndarray:
    return x / (np.std(x) or 1.0)


def _peak(x: np.ndarray) -> np.ndarray:
    return x / (np.max(np.abs(x)) or 1.0)


def _at(buf: np.ndarray, sig: np.ndarray, t: float, gain: float = 1.0) -> None:
    add_at(buf, sig * gain, int(round(t * SR)))


def _fit(x: np.ndarray, seconds: float) -> np.ndarray:
    n = samples(seconds)
    out = np.zeros(n)
    m = min(n, len(x))
    out[:m] = x[:m]
    return out


def _verb(x: np.ndarray, rng, seconds: float, wet: float, damp: float = 2600.0,
          predelay: float = 0.010, early=(), air: float = 4200.0) -> np.ndarray:
    """Dry sound (softened by `air`, a gentle high-frequency loss) plus a wet-only outdoor
    reflection (a short dark tail and a few slap-backs)."""
    x = lowpass(x, air, 2)
    ir = room_ir(seconds, rng, damp=damp, predelay=predelay, early=early)
    y = fir_convolve(x, ir)
    out = np.zeros(len(y))
    out[:len(x)] += x
    out += wet * y
    return out


def _onset(t: np.ndarray, rise: float) -> np.ndarray:
    """Raised-cosine onset over `rise` seconds. A grain that snaps on in a fraction of a
    millisecond is a click, and a cloud of clicks is the crackle this replaces."""
    return 0.5 - 0.5 * np.cos(np.pi * np.clip(t / rise, 0.0, 1.0))


def _tick(rng, f: float, tau: float, noisy: float = 0.5) -> np.ndarray:
    """One tiny resonant strike (a pebble, a chain link): a damped tone mixed with band noise."""
    ln = samples(min(tau * 7.0, 0.08))
    t = tvec(ln)
    tone = np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28))
    band = _unit(bandpass(noise(ln, rng), f * 0.7, min(f * 1.6, 8000.0), 2))
    return (tone * (1.0 - noisy) + band * noisy) * np.exp(-t / tau) * _onset(t, 0.001)


def _cloud(n: int, rng, count: int, t0: float, t1: float, flo: float, fhi: float,
           tau_lo: float, tau_hi: float, skew: float = 1.0, amp_lo: float = 0.2,
           noisy: float = 0.5, fall: float = 0.0) -> np.ndarray:
    """Granular micro-events. skew > 1 crowds the grains toward t0; fall fades later grains."""
    out = np.zeros(n)
    for u in np.sort(rng.random(count) ** skew):
        f = float(np.exp(rng.uniform(np.log(flo), np.log(fhi))))
        a = rng.uniform(amp_lo, 1.0) * (1.0 - fall * u)
        _at(out, _tick(rng, f, rng.uniform(tau_lo, tau_hi), noisy), t0 + (t1 - t0) * u, a)
    return out


def _burst(rng, dur: float, lo: float, hi: float, tau: float, attack: float = 0.0003,
           order: int = 2) -> np.ndarray:
    ln = samples(dur)
    return bandpass(noise(ln, rng), lo, hi, order) * decay(ln, tau, attack=attack)


def _hp_click(rng, dur: float, fc: float, tau: float) -> np.ndarray:
    ln = samples(dur)
    return _unit(lowpass(highpass(noise(ln, rng), fc), 7000.0, 2) * decay(ln, tau, attack=0.0005))


def _body(n: int, rng, fc: float, tau: float, modes=(), attack: float = 0.0015) -> np.ndarray:
    """Low weight: brown noise plus a few heavily damped low modes. Short, so never a 'boop'."""
    b = lowpass(brown(n, rng), fc) * decay(n, tau, attack=attack)
    if modes:
        fs, ds, amps = zip(*modes)
        b = _unit(b) * 0.8 + modal(fs, ds, amps, n, rng) * decay(n, tau * 2.5, attack=attack)
    return b


# ---------------------------------------------------------------- movement

_FOOT = {
    # heel->toe gap s, weight, leather brightness, strap tick level, grit count, ground lowpass
    1: dict(gap=0.034, w=1.00, bright=1.0, tick=0.0, grit=26, fc=170),
    2: dict(gap=0.050, w=0.85, bright=1.25, tick=0.55, grit=34, fc=200),
    3: dict(gap=0.028, w=1.12, bright=0.85, tick=0.0, grit=20, fc=150),
    4: dict(gap=0.042, w=0.92, bright=1.1, tick=0.8, grit=30, fc=185),
}


def _contact(rng, w: float, bright: float, fc: float, soft: float = 1.0) -> np.ndarray:
    """One boot contact on packed dirt: low thump, leather mid, a click of heel edge."""
    n = samples(0.12)
    out = _body(n, rng, fc, 0.026 * w, modes=((68 * w ** -0.3, 0.030, 0.6), (96, 0.022, 0.35),
                                              (133, 0.016, 0.25)))
    out = _unit(out) * 0.55 * w
    leather = bandpass(noise(n, rng), 280, 1100 * bright) * decay(n, 0.014, attack=0.0008)
    out += _unit(leather) * 0.5 * soft
    out += _hp_click_n(rng, n, 1800 * bright, 0.0016) * 0.22 * soft
    return out


def _hp_click_n(rng, n: int, fc: float, tau: float) -> np.ndarray:
    return _unit(lowpass(highpass(noise(n, rng), fc), 7000.0, 2) * decay(n, tau, attack=0.0005))


def footstep(variant: int) -> np.ndarray:
    rng = rng_for(f"footstep_{variant}")
    p = _FOOT[variant]
    dur = 0.30
    n = samples(dur)
    out = np.zeros(n)
    _at(out, _contact(rng, p["w"], p["bright"], p["fc"]), 0.004)
    _at(out, _contact(rng, p["w"] * 0.6, p["bright"] * 1.1, p["fc"] * 1.3, soft=0.8),
        0.004 + p["gap"], 0.62)
    # Grit crunch under and after the heel (dense then sparse), and the scuff of the sole rolling.
    out += _cloud(n, rng, p["grit"], 0.004, 0.17, 900, 3800, 0.0012, 0.004, skew=1.7,
                  amp_lo=0.1, noisy=0.8, fall=0.5) * 0.05
    out += _unit(bandpass(noise(n, rng), 220, 1500) * decay(n, 0.07, attack=0.015)) * 0.035
    if variant in (2, 3):  # one loose stone knocked on the way
        _at(out, _tick(rng, rng.uniform(2300, 3600), 0.006, 0.3), 0.004 + p["gap"] * 1.8, 0.07)
    if p["tick"]:  # strap buckle or mail ring ticking against the greave
        for k, f in enumerate((3700, 5200)):
            _at(out, _tick(rng, f * rng.uniform(0.95, 1.05), 0.012, 0.15), 0.006 + 0.011 * k,
                0.055 * p["tick"])
    out = _verb(out, rng, 0.12, 0.10, damp=2000.0, predelay=0.006, early=((0.011, 0.05),))
    return finalize(_fit(out, dur), 0.62, 0.0002, 0.06)


def land() -> np.ndarray:
    rng = rng_for("land")
    dur = 0.55
    n = samples(dur)
    out = np.zeros(n)
    for t, g in ((0.003, 1.0), (0.013, 0.8)):
        _at(out, _contact(rng, 1.45, 0.9, 135), t, g)
    out += _body(n, rng, 95, 0.07, modes=((52, 0.06, 0.7), (78, 0.05, 0.4))) * 0.4
    out += _cloud(n, rng, 70, 0.01, 0.30, 1000, 5500, 0.0015, 0.006, skew=1.5, amp_lo=0.1,
                  noisy=0.75, fall=0.4) * 0.06
    # Armour and mail rattle shaken by the landing, plate shifting on plate.
    out += _cloud(n, rng, 55, 0.02, 0.36, 2600, 8000, 0.003, 0.012, skew=1.3, amp_lo=0.1,
                  noisy=0.3, fall=0.7) * 0.04
    out += modal([380, 720], [0.03, 0.03], [0.18, 0.12], n, rng) * decay(n, 0.03, attack=0.004) * 0.15
    out = _verb(out, rng, 0.22, 0.14, damp=2200.0, predelay=0.012, early=((0.021, 0.07), (0.05, 0.04)))
    return finalize(_fit(out, dur), 0.85, 0.0002, 0.12)


def jump() -> np.ndarray:
    rng = rng_for("jump")
    dur = 0.36
    n = samples(dur)
    t = tvec(n)
    out = np.zeros(n)
    # Push off: sole scuff and a small grit kick.
    out += _unit(bandpass(noise(n, rng), 260, 1600) * decay(n, 0.028, attack=0.004)) * 0.45
    out += _cloud(n, rng, 14, 0.0, 0.07, 1500, 5000, 0.0015, 0.004, noisy=0.8) * 0.10
    # Effort breath "hff": breath through a loose mouth, quick rise, longer fall.
    src = noise(n, rng)
    breath = (resonator_tv(src, 750.0, 1.6) * 0.7 + resonator_tv(src, 1900.0, 2.2) * 0.5
              + highpass(src, 3500) * 0.02)
    env = np.where(t < 0.04, (t / 0.04) ** 1.6, np.exp(-(t - 0.04) / 0.065))
    env = np.where(t < 0.012, 0.0, env)
    out += _unit(breath) * env * 0.35
    # Cloth and leather rustle as the cloak swings, a faint buckle.
    out += _cloud(n, rng, 22, 0.015, 0.30, 1800, 6500, 0.004, 0.014, noisy=1.0) * 0.03
    out += modal([3900, 5600], [0.012, 0.01], [0.04, 0.025], n, rng) * decay(n, 0.02, attack=0.001)
    out = _verb(out, rng, 0.10, 0.08, damp=2500.0, predelay=0.008)
    return finalize(_fit(out, dur), 0.60, 0.004, 0.08)


# ---------------------------------------------------------------- swings

_SWING = {
    # duration, peak time, rise power, fall time constant, band base, band span, q
    1: dict(dur=0.30, tp=0.12, rise=2.4, fall=0.022, base=650.0, span=2500.0, q=1.3),
    2: dict(dur=0.36, tp=0.150, rise=2.2, fall=0.028, base=900.0, span=2300.0, q=1.7),
    3: dict(dur=0.48, tp=0.215, rise=2.0, fall=0.045, base=330.0, span=1300.0, q=1.0),
}


def swing(variant: int) -> np.ndarray:
    rng = rng_for(f"swing_{variant}")
    p = _SWING[variant]
    dur = p["dur"]
    n = samples(dur)
    t = tvec(n)
    tp = p["tp"]
    # Blade speed: builds over the arc, peaks, then is cut off quickly as the blade passes.
    speed = np.clip(np.where(t < tp, (t / tp) ** p["rise"], np.exp(-(t - tp) / p["fall"])), 0.0, 1.0)
    # Faster blade = louder (~speed^2) and the band climbs.
    centre = p["base"] + p["span"] * speed ** 0.8
    src = pink(n, rng) * 0.6 + noise(n, rng) * 0.4
    air = _unit(resonator_tv(src, centre, p["q"]) + 0.45 * resonator_tv(src, centre * 0.55, p["q"] * 0.8))
    air = onepole_lowpass_tv(onepole_lowpass_tv(air, centre * 1.8), centre * 1.8)  # roll the skirts off
    out = air * speed ** 2 * (1.0 + 0.25 * (jitter(n, rng, 0.5, 90.0) - 1.0))
    if variant == 1:
        # Leather and armour creak in the wind-up before the cut.
        cw = np.minimum(1.0, t / 0.02) * np.exp(-t / 0.07) * (t < 0.12)
        creak_f = 520 + 160 * np.sin(2 * np.pi * 23 * t) + 700 * t
        creak = resonator_tv(noise(n, rng), creak_f, 14.0) * jitter(n, rng, 0.8, 140.0)
        out += _unit(creak) * cw * 0.22
        out += _cloud(n, rng, 10, 0.0, 0.11, 2500, 6500, 0.004, 0.01, noisy=1.0) * 0.04
    elif variant == 2:
        # Steel edge: a thin whistle that follows the blade speed.
        whistle = _unit(resonator_tv(noise(n, rng), 2500 + 2200 * speed, 38.0))
        out += whistle * speed ** 3 * 0.30
    else:
        # Heavy weapon: a low rushing body of air under the whoosh.
        out += _unit(lowpass(brown(n, rng), 260) * speed ** 1.5) * 0.36
        out += _cloud(n, rng, 8, 0.0, 0.16, 300, 1200, 0.01, 0.03, noisy=1.0) * 0.03
    out = _verb(out, rng, 0.10, 0.07, damp=2800.0, predelay=0.006)
    return finalize(_fit(out, dur + 0.04), 0.74, 0.008, 0.07)


# ---------------------------------------------------------------- combat

_HIT = {
    # weight, meat mode scale, slap brightness, wet tail, layer offset s, duration
    1: dict(w=1.00, ms=1.00, slap=1.0, tail=0.14, off=0.0035, dur=0.36),
    2: dict(w=0.82, ms=1.18, slap=1.25, tail=0.10, off=0.0020, dur=0.30),
    3: dict(w=1.20, ms=0.85, slap=0.8, tail=0.20, off=0.0055, dur=0.42),
}


def hit_flesh(variant: int) -> np.ndarray:
    rng = rng_for(f"hit_{variant}")
    p = _HIT[variant]
    dur = p["dur"]
    n = samples(dur)
    w = p["w"]
    out = np.zeros(n)
    # 1-3 ms contact click, then the fur and hide slap (very short, 2-6 kHz).
    _at(out, _hp_click(rng, 0.01, 1500, 0.0007), 0.0, 0.5)
    _at(out, _unit(_burst(rng, 0.02, 2000, 6000 * p["slap"] ** 0.5, 0.0035 * w, 0.0002)), 0.001,
        0.38 * p["slap"])
    # Body thump: filtered brown noise plus damped low modes, a hair after the contact.
    bn = int(0.25 * SR)
    f = 52 + 26 / w
    body = _body(bn, rng, 120 / w ** 0.4, 0.045 * w,
                 modes=((f, 0.045, 0.7), (f * 1.52, 0.034, 0.45), (f * 2.1, 0.025, 0.25)))
    _at(out, _unit(body), p["off"], 0.8 * w)
    # Meaty mid band: damped low-Q modes excited by a noise burst.
    mid = modal([f_ * p["ms"] for f_ in (290, 470, 740, 1050)], (0.030, 0.026, 0.020, 0.014),
                (1.0, 0.8, 0.6, 0.4), bn, rng)
    mid *= 0.6 + 0.4 * lowpass(noise(bn, rng), 900)
    mid += _burst(rng, 0.25, 250, 1200, 0.020, 0.0008) * 0.6
    _at(out, _unit(mid), p["off"] * 0.6, 0.75)
    # Faint wet tail: soft, dark, with a few moist micro-clicks.
    _at(out, _unit(lowpass(noise(n, rng), 1500) * decay(n, 0.09, attack=0.012)), 0.0, p["tail"] * 0.5)
    out += _cloud(n, rng, 9, 0.02, 0.2, 500, 1800, 0.003, 0.008, noisy=0.8, fall=0.5) * p["tail"] * 0.5
    out = soft_clip(_peak(out), 1.6)
    out = _verb(out, rng, 0.22, 0.20, damp=2300.0, predelay=0.014, early=((0.026, 0.09), (0.061, 0.05)))
    return finalize(_fit(out, dur + 0.06), 0.88, 0.0002, 0.09)


def hit_critical() -> np.ndarray:
    rng = rng_for("hit_critical")
    dur = 0.72
    n = samples(dur)
    out = np.zeros(n)
    _at(out, _hp_click(rng, 0.01, 1200, 0.0009), 0.0, 0.7)
    # Bone crack: dense micro-transients plus bright modes.
    cr = np.zeros(samples(0.12))
    for s in np.sort(rng.random(34) ** 1.6) * 0.075:
        add_at(cr, _unit(_burst(rng, 0.006, 1500, 6500, 0.0012, 0.00008)) * rng.uniform(0.3, 1.0),
               int(s * SR))
    modes = modal((1620, 2240, 2870, 3410), (0.040, 0.030, 0.022, 0.016), (1.0, 0.8, 0.7, 0.5),
                  samples(0.2), rng)
    _at(out, cr, 0.002, 0.55)
    _at(out, _unit(modes), 0.003, 0.32)
    # Deep thud, saturated for weight.
    bn = int(0.5 * SR)
    body = _body(bn, rng, 95, 0.085, modes=((44, 0.10, 0.9), (62, 0.08, 0.7), (97, 0.05, 0.4)))
    _at(out, soft_clip(_peak(body), 2.6) * 2.4, 0.006)
    mid = modal((240, 410, 650, 930), (0.05, 0.04, 0.03, 0.02), (1.0, 0.8, 0.6, 0.4), bn, rng)
    mid += _burst(rng, 0.5, 200, 1100, 0.04, 0.001) * 0.7
    _at(out, _unit(mid), 0.004, 0.55)
    # Blade-bite scrape dragging through hide and bone.
    sn = samples(0.18)
    scr = resonator_tv(noise(sn, rng), np.linspace(2800, 1500, sn), 3.0) * decay(sn, 0.07, attack=0.01)
    scr *= jitter(sn, rng, 0.6, 400.0)
    _at(out, _unit(scr), 0.018, 0.22)
    _at(out, _unit(lowpass(noise(n, rng), 1200) * decay(n, 0.16, attack=0.02)), 0.0, 0.2)
    out += _cloud(n, rng, 16, 0.03, 0.35, 400, 2000, 0.004, 0.012, noisy=0.8, fall=0.6) * 0.18
    out = _verb(_peak(out), rng, 0.40, 0.26, damp=2000.0, predelay=0.016,
                early=((0.030, 0.10), (0.072, 0.06), (0.130, 0.035)))
    return finalize(_fit(out, dur + 0.05), 0.92, 0.0002, 0.15)


# ---------------------------------------------------------------- voice

def _glottal(f0: np.ndarray, rng, shimmer: float = 0.12, jit: float = 0.02,
             fry: np.ndarray | None = None) -> np.ndarray:
    """Glottal pulse train: period-by-period impulses with jitter, shimmer and optional vocal fry
    (period doubling and irregular long pulses where `fry` is high), tilted by one-pole lowpasses."""
    n = len(f0)
    src = np.zeros(n)
    pos = 0.0
    k = 0
    while pos < n - 2:
        i = int(pos)
        fr = 0.0 if fry is None else float(fry[i])
        period = SR / max(f0[i], 30.0) * (1.0 + jit * rng.uniform(-1, 1))
        amp = 1.0 + shimmer * rng.uniform(-1, 1)
        if fr > 0.0:
            if k % 2:
                amp *= 1.0 - 0.55 * fr
                period *= 1.0 + 0.35 * fr * rng.uniform(0, 1)
            else:
                period *= 1.0 + 0.25 * fr * rng.uniform(0, 1)
        frac = pos - i
        src[i] += amp * (1.0 - frac)
        src[i + 1] += amp * frac
        pos += period
        k += 1
    src = onepole_lowpass_tv(src, 2200.0)
    src = onepole_lowpass_tv(src, 3500.0)
    return src - onepole_lowpass_tv(src, 60.0)


def _vocal_tract(x: np.ndarray, formants) -> np.ndarray:
    """Parallel formant resonators: (centre Hz or track, q, gain)."""
    out = np.zeros(len(x))
    for fc, q, g in formants:
        out += g * resonator_tv(x, fc, q)
    return out


def _chain(n: int, rng, count: int, t0: float, t1: float, gain: float, skew: float = 1.0) -> np.ndarray:
    return _cloud(n, rng, count, t0, t1, 2800, 8000, 0.004, 0.016, skew=skew, amp_lo=0.1, noisy=0.25,
                  fall=0.5) * gain


def player_hurt() -> np.ndarray:
    rng = rng_for("player_hurt")
    dur = 0.40
    n = samples(dur)
    t = tvec(n)
    # A man's grunt through a clenched mouth: low and falling, strained at the start.
    f0 = (128.0 - 34.0 * np.clip(t / 0.30, 0, 1) ** 0.8) * jitter(n, rng, 0.03, 9.0)
    f0 *= 1.0 + 0.04 * np.exp(-t / 0.03)
    fry = np.clip((t - 0.17) / 0.12, 0.0, 1.0) * 0.9
    src = _glottal(f0, rng, shimmer=0.16, jit=0.025, fry=fry)
    f1 = 430.0 + 90.0 * np.exp(-t / 0.08)
    voice = _unit(_vocal_tract(src, [(f1, 5.0, 1.0), (1000.0 + 120 * np.exp(-t / 0.1), 7.0, 0.75),
                                     (2350.0, 9.0, 0.30), (3300.0, 10.0, 0.12)]))
    breath = _unit(_vocal_tract(noise(n, rng), [(f1, 2.0, 0.8), (1400.0, 1.5, 0.6), (2600.0, 2.0, 0.3)]))
    onset = np.where(t < 0.004, t / 0.004, 1.0)   # abrupt glottal onset
    env = onset * np.exp(-np.maximum(t - 0.05, 0) / 0.13) * (1.0 + 0.3 * np.exp(-t / 0.02))
    out = voice * env + breath * env * 0.45 * np.exp(-t / 0.2)
    # A quiet shiver of chainmail under the cry.
    out += _cloud(n, rng, 18, 0.0, 0.3, 2800, 7500, 0.005, 0.014, noisy=0.3) * 0.02 * env.max()
    out = soft_clip(_peak(out), 1.3)
    out = _verb(out, rng, 0.12, 0.10, damp=2500.0, predelay=0.010, early=((0.022, 0.05),), air=3200.0)
    return finalize(_fit(out, dur + 0.05), 0.80, 0.002, 0.07)


def player_death() -> np.ndarray:
    rng = rng_for("player_death")
    dur = 2.30
    n = samples(dur)
    out = np.zeros(n)
    # Agonal groan: falling pitch sliding into vocal fry, the jaw dropping open.
    gn = samples(1.0)
    t = tvec(gn)
    f0 = (118.0 - 52.0 * t ** 0.7) * jitter(gn, rng, 0.045, 7.0)
    f0 *= 1.0 + 0.03 * np.sin(2 * np.pi * 4.3 * t + 1.0)
    fry = np.clip((t - 0.30) / 0.45, 0.0, 1.0)
    src = _glottal(f0, rng, shimmer=0.20, jit=0.03, fry=fry)
    f1 = 380.0 + 260.0 * np.clip(t / 0.35, 0, 1) - 150.0 * np.clip((t - 0.5) / 0.5, 0, 1)
    voice = _unit(_vocal_tract(src, [(f1, 4.5, 1.0), (900.0 + 200 * t, 6.0, 0.7), (2300.0, 8.0, 0.25)]))
    breath = _unit(_vocal_tract(noise(gn, rng), [(f1, 1.8, 0.9), (1500.0, 1.4, 0.6), (2800.0, 2.0, 0.3)]))
    env = np.minimum(1.0, t / 0.012) * np.exp(-np.maximum(t - 0.25, 0) / 0.40)
    env *= 1.0 + 0.15 * np.sin(2 * np.pi * 5.1 * t)
    groan = voice * env + breath * env * 0.55
    # The last breath leaving the chest.
    gate = np.exp(-((t - 0.78) / 0.14) ** 2)
    exh = _unit(_vocal_tract(noise(gn, rng), [(700.0, 1.5, 0.8), (1600.0, 1.5, 0.5)]))
    groan += exh * 0.20 * gate
    _at(out, groan, 0.0, 0.6)
    # The body goes down: mail pours, plate knocks, then the heavy ground impact.
    out += _chain(n, rng, 85, 0.62, 1.20, 0.17, skew=1.3)
    kn = samples(0.2)
    for tt, f in ((0.70, 410), (0.78, 640), (0.92, 330)):
        _at(out, modal([f, f * 2.4], [0.05, 0.03], [1.0, 0.5], kn, rng) * decay(kn, 0.05, attack=0.002),
            tt, 0.08)
    bn = int(0.7 * SR)
    thud = _body(bn, rng, 85, 0.11, modes=((46, 0.13, 0.9), (64, 0.10, 0.6), (101, 0.06, 0.3)))
    _at(out, soft_clip(_peak(thud), 2.2), 0.90, 1.3)
    _at(out, _hp_click(rng, 0.02, 900, 0.003), 0.90, 0.6)
    out += _cloud(n, rng, 40, 0.90, 1.45, 800, 4800, 0.002, 0.008, skew=1.5, noisy=0.7, fall=0.5) * 0.10
    # Settling: a few last chain slips and a plate rocking to rest.
    for tt, a in ((1.22, 0.30), (1.36, 0.20), (1.62, 0.12), (1.74, 0.07)):
        _at(out, _tick(rng, rng.uniform(3200, 6500), 0.012, 0.2), tt, a * 0.28)
    out += _chain(n, rng, 22, 1.2, 1.9, 0.07, skew=2.0)
    out = _verb(_peak(out), rng, 0.55, 0.22, damp=2200.0, predelay=0.020, early=((0.04, 0.08), (0.11, 0.05)), air=3800.0)
    return finalize(_fit(out, dur), 0.88, 0.002, 0.30)


# ---------------------------------------------------------------- gear and world

def _scrape(n: int, rng, f_track: np.ndarray, q: float, rough: float, env: np.ndarray) -> np.ndarray:
    """Steel dragged through leather: noise excites a moving resonance, stick-slip chops it."""
    r = _unit(resonator_tv(noise(n, rng), f_track, q))
    slip = np.clip(jitter(n, rng, 1.0, 260.0), 0.0, None) ** rough
    grit = _unit(highpass(noise(n, rng), 3500))
    return (r * slip + 0.25 * grit * slip) * env


def equip() -> np.ndarray:
    rng = rng_for("equip")
    dur = 0.80
    n = samples(dur)
    out = np.zeros(n)
    sd = 0.42
    sn = samples(sd)
    st = tvec(sn)
    u = st / sd
    # Friction scrape of the blade leaving the scabbard: pitch rising, speed accelerating.
    env = np.minimum(1.0, st / 0.03) * (0.35 + 0.65 * u ** 1.2)
    _at(out, _scrape(sn, rng, 650.0 + 2300.0 * u ** 1.4, 7.0, 1.4, env), 0.03, 0.55)
    low = bandpass(noise(sn, rng), 120, 600) * (0.3 + u) * np.minimum(1.0, st / 0.05)
    _at(out, _unit(low), 0.03, 0.12)
    _at(out, _cloud(sn, rng, 28, 0.0, sd, 1500, 5000, 0.002, 0.006, noisy=0.8), 0.03, 0.05)
    # Leather throat of the scabbard releasing.
    _at(out, _unit(_burst(rng, 0.04, 200, 1200, 0.010, 0.001)), 0.0, 0.18)
    # Short inharmonic steel ring as the blade clears the mouth.
    rn = int(0.5 * SR)
    ratios = (1.0, 2.43, 4.1, 6.2, 8.7)
    ring = modal([1750 * r * rng.uniform(0.99, 1.01) for r in ratios], (0.20, 0.12, 0.07, 0.04, 0.025),
                 (1.0, 0.55, 0.35, 0.2, 0.1), rn, rng)
    ring *= np.minimum(1.0, tvec(rn) / 0.0008)
    _at(out, _unit(ring), 0.03 + sd - 0.01, 0.20)
    _at(out, _hp_click(rng, 0.01, 2500, 0.0012), 0.03 + sd - 0.01, 0.15)
    out = _verb(_peak(out), rng, 0.20, 0.10, damp=3000.0, predelay=0.010, early=((0.02, 0.05),), air=6500.0)
    return finalize(_fit(out, dur), 0.74, 0.003, 0.14)


def unequip() -> np.ndarray:
    rng = rng_for("unequip")
    dur = 0.50
    n = samples(dur)
    out = np.zeros(n)
    sd = 0.24
    sn = samples(sd)
    st = tvec(sn)
    u = st / sd
    # Sheathing: a shorter scrape, pitch falling as the blade slides home, then a dull clack.
    env = np.minimum(1.0, st / 0.04) * (1.0 - 0.4 * u)
    _at(out, _scrape(sn, rng, 1700.0 - 900.0 * u ** 0.8, 6.0, 1.5, env), 0.0, 0.45)
    _at(out, _cloud(sn, rng, 16, 0.0, sd, 1500, 4500, 0.002, 0.006, noisy=0.8), 0.0, 0.05)
    # Crossguard on the scabbard mouth: low stubby modes, a damped click.
    cn = int(0.25 * SR)
    clack = modal((540, 1180, 2450, 3900), (0.022, 0.016, 0.010, 0.006), (1.0, 0.7, 0.4, 0.2), cn, rng)
    clack += _burst(rng, 0.25, 300, 3000, 0.008, 0.0004) * 0.8
    _at(out, _unit(clack), sd - 0.005, 0.40)
    _at(out, _unit(_body(cn, rng, 220, 0.015)), sd - 0.005, 0.18)
    out = _verb(_peak(out), rng, 0.15, 0.09, damp=2800.0, predelay=0.008, air=5500.0)
    return finalize(_fit(out, dur), 0.68, 0.003, 0.10)


def block_break() -> np.ndarray:
    rng = rng_for("block_break")
    dur = 1.40
    n = samples(dur)
    out = np.zeros(n)
    # Fracture and the low impact of the block giving way.
    _at(out, _hp_click(rng, 0.03, 800, 0.004), 0.0, 0.6)
    bn = int(0.7 * SR)
    body = _body(bn, rng, 130, 0.12, modes=((58, 0.12, 0.8), (88, 0.08, 0.5), (140, 0.05, 0.3)))
    _at(out, soft_clip(_peak(body), 1.8), 0.004, 1.0)
    grind = lowpass(brown(bn, rng), 600) * decay(bn, 0.18, attack=0.02) * (0.5 + 0.5 * jitter(bn, rng, 0.9, 40.0))
    _at(out, _unit(grind), 0.01, 0.35)
    # Crumbling debris: pebble modes 1-5 kHz, dense early and thinning out.
    out += _cloud(n, rng, 170, 0.015, 0.75, 1000, 5000, 0.003, 0.014, skew=2.0, amp_lo=0.1,
                  noisy=0.45, fall=0.6) * 0.22
    # Larger chunks tumbling away, and the rolling settle (bouncing gaps shrink).
    gap, tt, amp = 0.11, 0.38, 0.55
    while gap > 0.012 and tt < dur - 0.2:
        _at(out, _tick(rng, rng.uniform(900, 2600), rng.uniform(0.008, 0.02), 0.5), tt, amp * 0.3)
        tt += gap
        gap *= rng.uniform(0.62, 0.8)
        amp *= 0.85
    out += _cloud(n, rng, 40, 0.45, 1.25, 1200, 4200, 0.002, 0.006, skew=1.6, amp_lo=0.1,
                  noisy=0.6, fall=0.6) * 0.07
    # Dust hiss drifting down.
    out += _unit(highpass(noise(n, rng), 3800) * decay(n, 0.30, attack=0.05)) * 0.045
    out = _verb(_peak(out), rng, 0.45, 0.20, damp=2300.0, predelay=0.018, early=((0.035, 0.08), (0.09, 0.05)), air=5200.0)
    return finalize(_fit(out, dur), 0.88, 0.0002, 0.2)


def block_crack() -> np.ndarray:
    rng = rng_for("block_crack")
    dur = 0.50
    n = samples(dur)
    out = np.zeros(n)
    # Sharp fracture: bright click, high stone modes, a cluster of micro-cracks.
    _at(out, _hp_click(rng, 0.02, 1800, 0.0010), 0.0, 0.9)
    hm = modal((2150, 3050, 4300, 5900), (0.040, 0.028, 0.018, 0.012), (1.0, 0.8, 0.6, 0.4), samples(0.2), rng)
    _at(out, _unit(hm), 0.0008, 0.45)
    for s, a in ((0.012, 0.6), (0.031, 0.5), (0.066, 0.35), (0.118, 0.25)):
        _at(out, _tick(rng, rng.uniform(1800, 5200), 0.004, 0.55), s, a * 0.55)
    # The stone body takes the shock.
    bn = samples(0.2)
    _at(out, _unit(_body(bn, rng, 220, 0.03, modes=((95, 0.04, 0.6), (170, 0.03, 0.4)))), 0.001, 0.40)
    # Short grinding creak as the crack opens: stick-slip resonance.
    gn = samples(0.3)
    gt = tvec(gn)
    gf = 520 + 380 * np.clip(gt / 0.25, 0, 1) + 60 * np.sin(2 * np.pi * 17 * gt)
    gr = resonator_tv(noise(gn, rng), gf, 9.0) * np.clip(jitter(gn, rng, 1.0, 180.0), 0, None) ** 1.5
    gr *= np.minimum(1.0, gt / 0.03) * np.exp(-gt / 0.11)
    _at(out, _unit(gr), 0.03, 0.26)
    out += _cloud(n, rng, 20, 0.02, 0.3, 1200, 4500, 0.002, 0.006, skew=1.6, noisy=0.7, fall=0.6) * 0.08
    out = _verb(_peak(out), rng, 0.22, 0.16, damp=3200.0, predelay=0.012, early=((0.028, 0.10), (0.066, 0.06)), air=6000.0)
    return finalize(_fit(out, dur), 0.80, 0.0002, 0.10)


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


# Highest frequency each sound keeps (Hz). Saturation and dense grain clouds leave fizz above
# this that reads as crackle; real dirt, flesh and leather heard outdoors have little up there.
_FIZZ_CAP = {
    "footstep": 5500.0, "land": 5500.0, "jump": 8000.0, "swing": 9000.0, "hit": 7500.0,
    "hit_critical": 8000.0, "player_hurt": 7000.0, "player_death": 7000.0,
    "equip": 6500.0, "unequip": 9000.0, "block_break": 5500.0, "block_crack": 6500.0,
}


def _defizz(x: np.ndarray, name: str) -> np.ndarray:
    """Steep low-pass at the sound's cap, then the usual peak, DC and edge treatment."""
    peak = float(np.max(np.abs(x))) or 1.0
    return finalize(lowpass(x, _FIZZ_CAP[name], 4), peak, 0.0003, 0.01)


def build(out_root: Path) -> list[Path]:
    written: list[Path] = []
    sfx = out_root / "sfx"
    for name, (fn, count) in VARIANTS.items():
        for v in range(1, count + 1):
            p = sfx / f"{name}_{v}.wav"
            write_wav(p, _defizz(fn(v), name), SR)
            written.append(p)
    for name, fn in SINGLES.items():
        p = sfx / f"{name}.wav"
        write_wav(p, _defizz(fn(), name), SR)
        written.append(p)
    return written
