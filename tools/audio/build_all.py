"""Generate every game sound into dream/audio and check it.

    python -I tools/audio/build_all.py [--check-only] [--plots DIR]

Generation runs every family module's build(out_root) -> list[Path] (FAMILIES below), then deletes
any *.wav under the out folder that no build() returned, so a renamed sound never lingers.

Checks (exit code 1 on failure): 16-bit PCM WAV, expected sample rate and channels, no clipping
(peak <= 0.97 of full scale), sensible length, one-shots start and end near silence, loops have no
seam jump (the wrap-around step is no bigger than the signal's own typical step), and the whole
folder stays within the 12 MB budget. Every file in PACK (the gothic pack) must exist. --plots writes a waveform PNG per file (needs Pillow).
"""
from __future__ import annotations

import argparse
import sys
import wave
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

AUDIO_ROOT = HERE.parent.parent / "dream" / "audio"
BUDGET_BYTES = 12 * 1024 * 1024
LOOP_DIRS = {"ambience", "music"}

# Generator modules, each exposing build(out_root: Path) -> list[Path] that writes its own files.
FAMILIES = ("gen_sfx", "sfx_combat", "sfx_wolf", "gen_music", "gen_ambience")

# The 30 sounds of the gothic pack, relative to the audio folder.
PACK = (
    "music/menu.wav", "music/world.wav", "ambience/wind.wav", "ambience/night.wav",
    "sfx/hit_1.wav", "sfx/hit_2.wav", "sfx/hit_3.wav", "sfx/hit_critical.wav",
    "sfx/swing_1.wav", "sfx/swing_2.wav", "sfx/swing_3.wav", "sfx/player_hurt.wav", "sfx/player_death.wav",
    "sfx/wolf_howl_1.wav", "sfx/wolf_howl_2.wav", "sfx/wolf_howl_3.wav",
    "sfx/wolf_growl_1.wav", "sfx/wolf_growl_2.wav", "sfx/wolf_hurt.wav", "sfx/wolf_death.wav",
    "sfx/footstep_1.wav", "sfx/footstep_2.wav", "sfx/footstep_3.wav", "sfx/footstep_4.wav",
    "sfx/land.wav", "sfx/jump.wav",
    "sfx/equip.wav", "sfx/unequip.wav", "sfx/block_break.wav", "sfx/block_crack.wav",
)
assert len(PACK) == 30 and len(set(PACK)) == 30

# Fizz guard for the pack's one-shots: energy above FIZZ_HZ must sit at least FIZZ_MAX_DB below the
# 40 Hz..2 kHz body. Saturation and dense click-like grain clouds once put it at -8..-20 dB, which
# plays as a crackle ("crisping") over hits, steps and growls.
FIZZ_HZ = 8000.0
FIZZ_MAX_DB = -30.0


def fizz_db(mono: np.ndarray, sr: int) -> float:
    power = np.abs(np.fft.rfft(mono)) ** 2
    freqs = np.fft.rfftfreq(len(mono), 1.0 / sr)
    body = float(power[(freqs > 40) & (freqs < 2000)].sum()) or 1e-18
    return 10.0 * np.log10(float(power[freqs >= FIZZ_HZ].sum()) / body + 1e-18)


def generate(out_root: Path) -> list[Path]:
    """Run every family build(), then delete any wav in out_root/{sfx,ambience,music} that no build() returned."""
    import importlib

    written: list[Path] = []
    for name in FAMILIES:
        written.extend(importlib.import_module(name).build(out_root))
    keep = {p.resolve() for p in written}
    assert len(keep) == len(written), "two families wrote the same file"
    # Sweep only the three audio folders, so a stray --out never deletes unrelated wavs.
    for stale in sorted(w for d in ("sfx", "ambience", "music") for w in (out_root / d).glob("*.wav")):
        if stale.resolve() not in keep:
            print(f"removed stale {stale.relative_to(out_root).as_posix()}")
            stale.unlink()
    return written


def read_wav(path: Path) -> tuple[np.ndarray, int, int]:
    with wave.open(str(path), "rb") as w:
        assert w.getsampwidth() == 2, f"{path.name}: not 16-bit"
        ch, sr, frames = w.getnchannels(), w.getframerate(), w.getnframes()
        raw = np.frombuffer(w.readframes(frames), dtype="<i2").astype(np.float64) / 32768.0
    return (raw.reshape(-1, ch) if ch > 1 else raw), sr, ch


def check(root: Path) -> list[str]:
    problems: list[str] = []
    total = 0
    files = sorted(root.rglob("*.wav"))
    if not files:
        problems.append(f"no wav files under {root}")
    print(f"{'file':34} {'sr':>6} {'ch':>2} {'sec':>6} {'peak':>6} {'rms':>6} {'edge':>7}  note")
    for path in files:
        total += path.stat().st_size
        data, sr, ch = read_wav(path)
        rel = path.relative_to(root).as_posix()
        folder = path.parent.name
        mono = data if ch == 1 else data.mean(axis=1)
        seconds = len(mono) / sr
        peak = float(np.max(np.abs(data)))
        rms = float(np.sqrt(np.mean(data ** 2)))
        loop = folder in LOOP_DIRS
        note = "loop" if loop else "one-shot"
        if loop:
            seam = float(np.max(np.abs(data[0] - data[-1])))
            step = float(np.sqrt(np.mean(np.diff(mono) ** 2))) or 1e-9
            edge = seam / step
            if edge > 4.0:
                problems.append(f"{rel}: loop seam jump {edge:.1f}x the typical step")
            if not 4.0 <= seconds <= 40.0:
                problems.append(f"{rel}: loop length {seconds:.1f}s outside 4..40 s")
        else:
            edge = max(abs(float(mono[0])), abs(float(mono[-1])))
            if edge > 0.02:
                problems.append(f"{rel}: one-shot edge {edge:.3f} is not silent (click risk)")
            if not 0.04 <= seconds <= 6.0:
                problems.append(f"{rel}: length {seconds:.2f}s outside 0.04..6 s")
        if rel in PACK and folder == "sfx":
            fizz = fizz_db(mono, sr)
            if fizz > FIZZ_MAX_DB:
                problems.append(f"{rel}: fizz above {FIZZ_HZ:.0f} Hz is {fizz:.1f} dB vs the body "
                                f"(max {FIZZ_MAX_DB:.0f} dB; it plays as crackle)")
        if peak > 0.97:
            problems.append(f"{rel}: peak {peak:.3f} too close to clipping")
        if peak < 0.2:
            problems.append(f"{rel}: peak {peak:.3f} too quiet")
        expected_sr = {"sfx": 44100, "ambience": 22050, "music": 22050}.get(folder)
        if expected_sr and sr != expected_sr:
            problems.append(f"{rel}: sample rate {sr} != {expected_sr}")
        if folder == "music" and ch != 2:
            problems.append(f"{rel}: music must be stereo")
        if folder != "music" and ch != 1:
            problems.append(f"{rel}: must be mono")
        print(f"{rel:34} {sr:>6} {ch:>2} {seconds:>6.2f} {peak:>6.3f} {rms:>6.3f} {edge:>7.3f}  {note}")
    print(f"total {total / 1024 / 1024:.2f} MB in {len(files)} files (budget {BUDGET_BYTES / 1024 / 1024:.0f} MB)")
    if total > BUDGET_BYTES:
        problems.append(f"audio folder is {total} bytes, over the {BUDGET_BYTES} byte budget")
    for rel in PACK:
        if not (root / rel).is_file():
            problems.append(f"{rel}: missing from the gothic pack")
    return problems


def plots(root: Path, out_dir: Path) -> None:
    from PIL import Image, ImageDraw

    out_dir.mkdir(parents=True, exist_ok=True)
    for path in sorted(root.rglob("*.wav")):
        data, sr, ch = read_wav(path)
        mono = data if ch == 1 else data.mean(axis=1)
        width, height = 900, 160
        img = Image.new("RGB", (width, height), (16, 14, 14))
        draw = ImageDraw.Draw(img)
        draw.line([(0, height // 2), (width, height // 2)], fill=(60, 50, 50))
        for x in range(width):
            a = int(x * len(mono) / width)
            b = max(a + 1, int((x + 1) * len(mono) / width))
            seg = mono[a:b]
            lo, hi = float(seg.min()), float(seg.max())
            draw.line([(x, height // 2 - hi * (height / 2 - 4)), (x, height // 2 - lo * (height / 2 - 4))], fill=(200, 170, 120))
        img.save(out_dir / (path.stem + ".png"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check-only", action="store_true", help="validate existing files without regenerating")
    parser.add_argument("--out", type=Path, default=AUDIO_ROOT)
    parser.add_argument("--plots", type=Path, default=None)
    args = parser.parse_args()
    if not args.check_only:
        generate(args.out)
    problems = check(args.out)
    if args.plots:
        plots(args.out, args.plots)
    for p in problems:
        print("PROBLEM:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
