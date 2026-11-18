"""Wolf voice one-shots for dream (dark, grim, gothic). Original synthesis only.

Owned by the wolf workstream. Run through build_all.py. Output: 7 files in dream/audio/sfx
(44.1 kHz mono): wolf_howl_1..3, wolf_growl_1..2, wolf_hurt, wolf_death.

The voice is a real source-filter model: a Rosenberg-style glottal pulse train (flow derivative)
with f0 jitter, per-period shimmer and optional period doubling, plus aspiration noise that
follows the glottal flow, driven through time-varying vocal-tract resonators. No additive sines.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from synth import (SR, bandpass, finalize, fir_convolve, highpass, jitter, lowpass, modal,
                   noise, onepole_lowpass_tv, resonator_tv, rng_for, room_ir, samples,
                   soft_clip, tvec, write_wav)


# ---------------------------------------------------------------- private helpers

def _smooth(x: np.ndarray, hz: float) -> np.ndarray:
    """Zero-lag moving-average smoothing with edge padding (no circular wrap)."""
    w = max(3, int(SR / hz)) | 1
    win = np.hanning(w)
    win /= win.sum()
    pad = np.pad(x, (w, w), mode="edge")
    return np.convolve(pad, win, mode="same")[w:-w]


def _curve(n: int, knots_t, knots_v, smooth_hz: float = 6.0) -> np.ndarray:
    t = tvec(n)
    return _smooth(np.interp(t, knots_t, knots_v), smooth_hz)


def _wander(n: int, rng: np.random.Generator, hz: float) -> np.ndarray:
    """Slow irregular drift, unit standard deviation."""
    w = _smooth(rng.standard_normal(n), hz)
    return w / (np.std(w) or 1.0)


def _pitch(base: np.ndarray, rng: np.random.Generator, drift_c: float, vib_c: float,
           vib_hz: float = 5.0, onset: float = 1.0) -> np.ndarray:
    """f0 track: base Hz with slow irregular drift (cents) and an irregular, wandering vibrato."""
    n = len(base)
    t = tvec(n)
    drift = _wander(n, rng, 1.3) * drift_c
    rate = vib_hz * (1 + 0.18 * _wander(n, rng, 0.9))
    vib = np.sin(2 * np.pi * np.cumsum(rate) / SR + rng.uniform(0, 6.28))
    depth = vib_c * np.clip(t / onset, 0, 1) * (0.7 + 0.3 * _wander(n, rng, 0.8))
    return base * 2.0 ** ((drift + vib * depth) / 1200.0)


def _glottal(f0: np.ndarray, rng: np.random.Generator, oq: float = 0.65, jit: float = 0.01,
             shim: float = 0.08, sub: float = 0.0, speed: float = 0.25):
    """Glottal pulse train. Returns (flow derivative, flow) both ~unit scale.

    oq is the open quotient, speed the closing-phase fraction of the open phase (smaller is a
    sharper closure and a brighter source), jit/shim irregularity of period and amplitude, sub
    the depth of period doubling (alternate pulses weaker and slightly late).
    """
    n = len(f0)
    f = f0 * jitter(n, rng, jit, 90.0)
    ph = np.cumsum(f) / SR
    k = np.floor(ph).astype(np.int64)
    if sub > 0:
        odd = (k % 2).astype(np.float64)
        f = f * (1 + 0.03 * sub * (odd * 2 - 1))
        ph = np.cumsum(f) / SR
        k = np.floor(ph).astype(np.int64)
    u = ph - k
    per_amp = 1 + shim * rng.uniform(-1, 1, k[-1] + 2)
    amp = per_amp[k]
    if sub > 0:
        amp = amp * (1 - sub * (k % 2))
    tp = oq * (1 - speed)
    tn = oq * speed
    flow = np.where(u < tp, 0.5 * (1 - np.cos(np.pi * u / tp)), 0.0)
    close = (u >= tp) & (u < tp + tn)
    flow = np.where(close, np.cos(0.5 * np.pi * (u - tp) / tn), flow)
    flow = flow * amp
    d = np.diff(flow, prepend=0.0) * SR / np.maximum(f0, 1.0)
    d /= np.std(d) or 1.0
    return d, flow


def _tract(src: np.ndarray, formants) -> np.ndarray:
    """Parallel vocal-tract resonators: (centre Hz or track, q, gain)."""
    out = np.zeros(len(src))
    for fc, q, g in formants:
        out += g * resonator_tv(src, fc, q)
    return out


def _gate(n: int, rng: np.random.Generator, rate: float, depth: float, jit_amt: float = 0.5) -> np.ndarray:
    """Irregular pulsing amplitude (rattle / growl roughness), 1 at the top, 1-depth at the dips."""
    r = rate * jitter(n, rng, jit_amt, rate * 0.7)
    ph = np.cumsum(r) / SR
    return 1 - depth * (0.5 - 0.5 * np.cos(2 * np.pi * ph))


def _place(buf: np.ndarray, sig: np.ndarray, start: float) -> None:
    i = samples(start)
    end = min(len(buf), i + len(sig))
    if end > i:
        buf[i:end] += sig[: end - i]


# ---------------------------------------------------------------- howls

def _howl_voice(name: str, dur: float, knots_t, knots_f, *, vib_c: float, drift_c: float,
                onset: float, release: float, breath: float, f1: float, brightness: float,
                break_at: float | None = None, break_semis: float = 0.0, waver: float = 0.18):
    """One dry howling wolf. Fundamental-dominated: the source is low-passed just above f0."""
    rng = rng_for(name)
    n = samples(dur)
    t = tvec(n)
    base = _curve(n, knots_t, knots_f, 3.0)
    f0 = _pitch(base, rng, drift_c, vib_c, vib_hz=4.6, onset=1.1)
    amp = np.clip(t / onset, 0, 1) ** 1.6 * np.clip((dur - t) / release, 0, 1) ** 1.4
    amp = amp * (1 - waver * np.clip(0.5 + 0.5 * _wander(n, rng, 2.2), 0, 1))
    db = None
    if break_at is not None:
        # A pitch break: the voice cracks, drops out for a moment and lands a few semitones away.
        db = np.exp(-0.5 * ((t - break_at) / 0.045) ** 2)
        step = 1 / (1 + np.exp(-(t - break_at) / 0.012))
        f0 = f0 * 2.0 ** (break_semis * step / 12.0)
        amp = amp * (1 - 0.85 * db)
    d, flow = _glottal(f0, rng, oq=0.85, jit=0.004, shim=0.05, speed=0.7)
    asp = noise(n, rng) * (0.25 + flow) * breath
    if db is not None:
        asp = asp + noise(n, rng) * db * breath * 2.0
    src = d + asp
    body = onepole_lowpass_tv(onepole_lowpass_tv(src, f0 * 1.6), f0 * 2.2)
    tract = _tract(src, [(f1 * np.ones(n), 4.0, 0.10 * brightness),
                         (2.0 * f1 * np.ones(n), 5.0, 0.05 * brightness)])
    # Nothing in a wolf's throat lives below its fundamental: drop the sub-f0 rumble.
    voice = highpass(body + tract, 170, 2) * amp
    return voice, rng


def _distance(dry: np.ndarray, rng: np.random.Generator, *, air_hz: float, wet: float,
              direct: float, tail: float, predelay: float, echo: float, total: float,
              damp: float = 1400.0) -> np.ndarray:
    """Air absorption, then outdoor reverb with a late hillside echo."""
    far = onepole_lowpass_tv(onepole_lowpass_tv(dry, air_hz), air_hz)
    ir = room_ir(tail, rng, damp=damp, predelay=predelay,
                 early=((0.12, echo), (0.37, echo * 0.45)))
    w = fir_convolve(far, ir)
    out = np.zeros(samples(total))
    m = min(len(out), len(far))
    out[:m] += far[:m] * direct
    m = min(len(out), len(w))
    out[:m] += w[:m] * wet
    return out


def howl_1() -> np.ndarray:
    # A lone wolf, long and mournful: scooped rise, high wavering sustain, long falling tail.
    dry, rng = _howl_voice(
        "wolf_howl_1", 4.3, [0, 0.35, 0.9, 2.2, 3.3, 4.3], [330, 420, 560, 585, 540, 360],
        vib_c=16, drift_c=28, onset=0.8, release=1.3, breath=0.05, f1=900, brightness=1.0)
    out = _distance(dry, rng, air_hz=1700, wet=0.55, direct=0.5, tail=1.9, predelay=0.26,
                    echo=0.35, total=5.6)
    return finalize(out, 0.74, 0.01, 0.9)


def howl_2() -> np.ndarray:
    # A pack answering from far off: three wolves at different pitches and entrances.
    total = samples(5.5)
    mix = np.zeros(total)
    rng = rng_for("wolf_howl_2_space")
    specs = [
        ("wolf_howl_2a", 0.0, 4.0, [0, 0.5, 1.0, 2.2, 4.0], [300, 400, 470, 490, 330], 1.0),
        ("wolf_howl_2b", 0.7, 3.7, [0, 0.4, 0.9, 2.0, 3.7], [420, 540, 640, 660, 440], 0.8),
        ("wolf_howl_2c", 1.5, 3.4, [0, 0.4, 1.0, 1.9, 3.4], [360, 470, 520, 500, 300], 0.65),
    ]
    for name, start, dur, kt, kf, g in specs:
        dry, _ = _howl_voice(name, dur, kt, kf, vib_c=18, drift_c=40, onset=0.9, release=1.2,
                             breath=0.06, f1=880, brightness=0.9)
        far = _distance(dry, rng, air_hz=1250, wet=0.8, direct=0.3, tail=2.0, predelay=0.3,
                        echo=0.3, total=5.5, damp=1100)
        _place(mix, far * g, start)
    return finalize(mix, 0.72, 0.02, 0.9)


def howl_3() -> np.ndarray:
    # Closer, lower, descending howl that cracks and breaks downward partway.
    dry, rng = _howl_voice(
        "wolf_howl_3", 3.9, [0, 0.3, 0.8, 1.7, 2.6, 3.9], [330, 470, 480, 450, 360, 230],
        vib_c=14, drift_c=22, onset=0.55, release=1.1, breath=0.10, f1=760, brightness=1.2,
        break_at=1.75, break_semis=-3.2, waver=0.25)
    out = _distance(dry, rng, air_hz=2600, wet=0.42, direct=0.8, tail=1.6, predelay=0.22,
                    echo=0.3, total=5.3, damp=1700)
    return finalize(out, 0.78, 0.01, 0.7)


# ---------------------------------------------------------------- growls

def _inhale(dur: float, rng: np.random.Generator, level: float) -> np.ndarray:
    n = samples(dur)
    t = tvec(n)
    sig = bandpass(noise(n, rng), 500, 3200) + 0.5 * bandpass(noise(n, rng), 1800, 5000)
    env = np.clip(t / (dur * 0.7), 0, 1) ** 1.5 * np.clip((dur - t) / (dur * 0.3), 0, 1)
    return sig * env * level


def _growl_phrase(dur: float, f0a: float, f0b: float, rng: np.random.Generator,
                  *, sub: float, rough: float, formant_shift: float = 1.0) -> np.ndarray:
    n = samples(dur)
    t = tvec(n)
    base = _curve(n, [0, dur * 0.3, dur], [f0a, f0b * 1.08, f0b], 5.0)
    f0 = base * (1 + 0.05 * _wander(n, rng, 6.0)) * 2.0 ** (14 * _wander(n, rng, 1.6) / 1200)
    d, flow = _glottal(f0, rng, oq=0.45, jit=0.05, shim=0.25, sub=sub, speed=0.2)
    gate = _gate(n, rng, 23.0, rough, 0.6)
    turb = noise(n, rng) * (0.15 + 0.85 * np.abs(flow)) * 0.45
    src = (d + turb) * gate
    glide = _curve(n, [0, dur * 0.5, dur], [1.0, 1.12, 0.92], 3.0) * formant_shift
    voice = _tract(src, [(350 * glide, 5.0, 1.0), (780 * glide, 6.0, 0.85),
                         (1450 * glide, 7.0, 0.35), (2400 * np.ones(n), 6.0, 0.12)])
    voice = voice + 0.4 * lowpass(src, 180, 2)
    env = np.clip(t / 0.09, 0, 1) ** 1.2 * np.clip((dur - t) / 0.22, 0, 1) ** 1.1
    env = env * (0.85 + 0.15 * _wander(n, rng, 3.0))
    return voice * env


def _growl(name: str, hiss: bool) -> np.ndarray:
    rng = rng_for(name)
    total = 1.55 if hiss else 1.35
    out = np.zeros(samples(total))
    if hiss:
        # Lip-curled snarl: a bared-teeth hiss rises before the throat sound.
        hn = samples(0.3)
        ht = tvec(hn)
        h = highpass(noise(hn, rng), 2800, 2) + 0.4 * bandpass(noise(hn, rng), 900, 2400)
        henv = np.clip(ht / 0.18, 0, 1) ** 1.3 * np.clip((0.3 - ht) / 0.1, 0, 1)
        _place(out, h * henv * 0.22, 0.0)
        _place(out, _growl_phrase(0.62, 74, 88, rng, sub=0.35, rough=0.55, formant_shift=0.95), 0.12)
        _place(out, _inhale(0.16, rng, 0.07), 0.78)
        _place(out, _growl_phrase(0.62, 96, 66, rng, sub=0.5, rough=0.6), 0.92)
    else:
        _place(out, _growl_phrase(0.6, 66, 82, rng, sub=0.25, rough=0.5), 0.0)
        _place(out, _inhale(0.14, rng, 0.07), 0.6)
        _place(out, _growl_phrase(0.58, 90, 62, rng, sub=0.45, rough=0.6, formant_shift=1.04), 0.76)
    out = soft_clip(out / (np.max(np.abs(out)) or 1.0), 1.3)
    # Close in a damp wood/earth space: a few early reflections, short dark tail.
    ir = room_ir(0.25, rng, damp=1800, predelay=0.012, early=((0.009, 0.18), (0.021, 0.1)))
    wet = fir_convolve(out, ir)
    mix = np.zeros(len(wet))
    mix[: len(out)] += out
    mix += wet * 0.12
    return finalize(mix[: samples(total)], 0.84, 0.01, 0.12)


def growl_1() -> np.ndarray:
    return _growl("wolf_growl_1", hiss=False)


def growl_2() -> np.ndarray:
    return _growl("wolf_growl_2", hiss=True)


# ---------------------------------------------------------------- hurt and death

def _yelp(rng: np.random.Generator, dur: float, f_peak: float, f_end: float, *, jit: float,
          breath: float, rise: float = 0.025, rough: float = 0.0, sub: float = 0.0) -> np.ndarray:
    n = samples(dur)
    t = tvec(n)
    base = _curve(n, [0, rise, dur * 0.4, dur], [f_peak * 0.68, f_peak, f_peak * 0.88, f_end], 40.0)
    f0 = base * (1 + 0.03 * _wander(n, rng, 20.0)) * (
        1 + 0.012 * np.sin(2 * np.pi * np.cumsum(np.full(n, 31.0)) / SR))
    d, flow = _glottal(f0, rng, oq=0.55, jit=jit, shim=0.2, sub=sub, speed=0.3)
    asp = noise(n, rng) * (0.2 + flow) * breath
    src = d * (1 - 0.5 * rough * (1 - _gate(n, rng, 60.0, 1.0, 0.6))) + asp
    open_ = _curve(n, [0, rise * 2, dur], [0.9, 1.1, 0.8], 10.0)
    voice = _tract(src, [(950 * open_, 4.0, 1.0), (2100 * open_, 5.0, 0.6), (3300 * np.ones(n), 5.0, 0.25)])
    voice = voice + 0.25 * lowpass(src, 700, 2)
    env = np.clip(t / 0.003, 0, 1) * np.exp(-t / (dur * 0.55)) * np.clip((dur - t) / (dur * 0.2), 0, 1)
    return voice * env


def _whimper(rng: np.random.Generator, dur: float, f_a: float, f_b: float, breath: float) -> np.ndarray:
    n = samples(dur)
    t = tvec(n)
    base = _curve(n, [0, dur * 0.3, dur], [f_a, f_a * 1.08, f_b], 20.0)
    f0 = base * (1 + 0.04 * _wander(n, rng, 12.0))
    d, flow = _glottal(f0, rng, oq=0.75, jit=0.03, shim=0.2, speed=0.5)
    asp = noise(n, rng) * (0.3 + flow) * breath
    src = d * 0.7 + asp
    voice = _tract(src, [(780 * np.ones(n), 4.0, 1.0), (1700 * np.ones(n), 5.0, 0.4)])
    voice += 0.5 * onepole_lowpass_tv(src, 900)
    env = np.sin(np.pi * np.clip(t / dur, 0, 1)) ** 1.2
    return voice * env


def hurt() -> np.ndarray:
    rng = rng_for("wolf_hurt")
    out = np.zeros(samples(0.58))
    _place(out, _yelp(rng, 0.27, 1050, 760, jit=0.045, breath=0.7, rough=0.5, sub=0.3), 0.0)
    # The bark of the strike itself: a few ms of noisy, uneven attack under the yelp.
    bn = samples(0.03)
    bark = bandpass(noise(bn, rng), 900, 4200) * np.exp(-tvec(bn) / 0.009) * np.minimum(1, tvec(bn) / 0.001)
    _place(out, bark * 0.5, 0.0)
    _place(out, _whimper(rng, 0.17, 640, 470, 0.5) * 0.28, 0.26)
    _place(out, _whimper(rng, 0.13, 560, 400, 0.6) * 0.17, 0.42)
    out = soft_clip(out / (np.max(np.abs(out)) or 1.0), 1.5)
    ir = room_ir(0.2, rng, damp=2200, predelay=0.01, early=((0.014, 0.12),))
    wet = fir_convolve(out, ir)
    mix = wet[: len(out)] * 0.1 + out
    return finalize(mix, 0.84, 0.002, 0.07)


def _thud(rng: np.random.Generator, length: float = 0.55) -> np.ndarray:
    n = samples(length)
    t = tvec(n)
    # Body: a heavy low modal response, no clean sine sweep; mid knock of ribs and flank.
    body = modal([52, 78, 118, 171, 240], [0.09, 0.07, 0.05, 0.035, 0.03], [1.0, 0.7, 0.5, 0.3, 0.2], n, rng)
    body = body * np.minimum(1, t / 0.002)
    # Fur and cloth-like slap, and earth: a dull noise burst plus loose grit grains.
    slap = lowpass(noise(n, rng), 700, 2) * np.exp(-t / 0.05) * 0.8
    fur = bandpass(noise(n, rng), 250, 2200) * np.exp(-t / 0.11) * np.minimum(1, t / 0.006) * 0.25
    grit = np.zeros(n)
    for _ in range(34):
        s = rng.uniform(0.01, 0.4)
        gn = samples(rng.uniform(0.002, 0.007))
        g = bandpass(noise(gn, rng), 300, 3000) * np.hanning(gn) * rng.uniform(0.05, 0.2) * np.exp(-s / 0.18)
        _place(grit, g, s)
    return soft_clip((body + slap + fur + grit) / 2.2, 1.6) * 2.0


def death() -> np.ndarray:
    rng = rng_for("wolf_death")
    out = np.zeros(samples(2.1))
    # 1. The yelp.
    _place(out, _yelp(rng, 0.3, 960, 640, jit=0.04, breath=0.6, rough=0.4), 0.0)
    # 2. Descending choked whine, increasingly unstable, gated by failing breath.
    wd = 0.85
    n = samples(wd)
    t = tvec(n)
    base = _curve(n, [0, 0.15, 0.5, wd], [640, 600, 400, 190], 20.0)
    f0 = base * (1 + 0.025 * _wander(n, rng, 8.0) * (0.4 + t / wd))
    d, flow = _glottal(f0, rng, oq=0.7, jit=0.05, shim=0.3, sub=0.5, speed=0.45)
    asp = noise(n, rng) * (0.3 + flow) * 0.45
    src = d * (1 - 0.4 * (t / wd)) + asp
    glide = _curve(n, [0, wd], [1.0, 0.75], 3.0)
    whine = _tract(src, [(800 * glide, 4.0, 1.0), (1700 * glide, 5.0, 0.45)]) + 0.5 * onepole_lowpass_tv(src, 800)
    choke = _gate(n, rng, 9.0, 0.75, 0.6) ** 1.2
    wenv = np.clip(t / 0.05, 0, 1) * np.clip((wd - t) / 0.25, 0, 1) * np.exp(-t / 0.7)
    _place(out, whine * choke * wenv * 0.38, 0.24)
    # 3. The body hits the ground.
    _place(out, _thud(rng) * 0.62, 0.98)
    # 4. A final rattling exhale: wet, throaty, fading.
    en = samples(0.7)
    et = tvec(en)
    ex = bandpass(noise(en, rng), 250, 1600) + 0.5 * bandpass(noise(en, rng), 1600, 3600)
    ex = ex * _gate(en, rng, 26.0, 0.8, 0.5)
    ex = onepole_lowpass_tv(ex, _curve(en, [0, 0.7], [2400, 700], 5.0))
    eenv = np.clip(et / 0.06, 0, 1) ** 1.2 * np.exp(-et / 0.28)
    _place(out, ex * eenv * 0.3, 1.08)
    out = soft_clip(out / (np.max(np.abs(out)) or 1.0), 1.4)
    ir = room_ir(0.5, rng, damp=1600, predelay=0.02, early=((0.02, 0.1),))
    wet = fir_convolve(out, ir)
    mix = np.zeros(len(wet))
    mix[: len(out)] += out
    mix += wet * 0.1
    return finalize(mix[: samples(2.2)], 0.86, 0.002, 0.25)


# ---------------------------------------------------------------- build

FILES = {
    "wolf_howl_1": howl_1,
    "wolf_howl_2": howl_2,
    "wolf_howl_3": howl_3,
    "wolf_growl_1": growl_1,
    "wolf_growl_2": growl_2,
    "wolf_hurt": hurt,
    "wolf_death": death,
}


# Highest frequency each wolf sound keeps (Hz). The final saturation and the growl's pulse rattle
# leave fizz above this that reads as crackle; the howls are already dark with distance.
_FIZZ_CAP = {
    "wolf_howl_1": 8000.0, "wolf_howl_2": 8000.0, "wolf_howl_3": 8000.0,
    "wolf_growl_1": 5500.0, "wolf_growl_2": 6000.0, "wolf_hurt": 7000.0, "wolf_death": 6500.0,
}


def _defizz(x: np.ndarray, name: str) -> np.ndarray:
    """Steep low-pass at the sound's cap, then the usual peak, DC and edge treatment."""
    peak = float(np.max(np.abs(x))) or 1.0
    return finalize(lowpass(x, _FIZZ_CAP[name], 4), peak, 0.0003, 0.01)


def build(out_root: Path) -> list[Path]:
    written: list[Path] = []
    sfx = out_root / "sfx"
    for name, fn in FILES.items():
        p = sfx / f"{name}.wav"
        write_wav(p, _defizz(fn(), name), SR)
        written.append(p)
    return written
