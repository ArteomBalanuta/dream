# Procedural audio

Every sound in `dream/audio` is synthesised here: noise, sines, additive partials and spectral
filtering with numpy and the standard `wave` module. Nothing is sampled or downloaded, and every
generator seeds its own random stream from the sound's name, so a rebuild is byte-identical.

```
python tools/audio/build_all.py            # regenerate dream/audio/** and check it
python tools/audio/build_all.py --check-only
python tools/audio/build_all.py --plots artifacts/audio-report   # waveform PNGs (needs Pillow)
```

| Folder | Format | Contents |
| --- | --- | --- |
| `dream/audio/sfx` | 16-bit PCM, 44.1 kHz, mono | one-shots; variants are `name_1.wav`, `name_2.wav`, ... |
| `dream/audio/ambience` | 16-bit PCM, 22.05 kHz, mono | `wind`, `night` (seamless 8 s loops) |
| `dream/audio/music` | 16-bit PCM, 22.05 kHz, stereo | `world`, `menu` (seamless 20 s loops) |

Generators are split by sound family; each module exposes `build(out_root: Path) -> list[Path]`, and
`build_all.py` runs them all and then deletes any `.wav` no family wrote (so a renamed sound never
lingers):

| Module | Writes |
| --- | --- |
| `sfx_combat.py` | hits, criticals, swings, player hurt and death, footsteps, land, jump, equip, unequip, block break and crack |
| `sfx_wolf.py` | `wolf_howl_1..3`, `wolf_growl_1..2`, `wolf_hurt`, `wolf_death` |
| `gen_music.py` | `music/menu`, `music/world` |
| `gen_ambience.py` | `ambience/wind`, `ambience/night` |
| `gen_sfx.py` | the remaining one-shots (block place, loot, potion, level up, respawn, stat spend, UI, inventory) |

`synth.py` holds the shared helpers; `python -I tools/audio/synth.py` runs their self-check, and
`spectrogram_png` draws a log-frequency spectrogram for inspecting a sound by eye.

File names follow `Dream.Client.Audio.SoundId` in snake_case (`HitCritical` is `hit_critical.wav`).
`build_all.py` fails on clipping, a click at a one-shot's ends, a loop seam jump, a wrong format, or
more than 12 MB in total, or a missing file of the 30-sound gothic pack (`PACK`). Loops are periodic by construction (circular filters and reverb, partials on
whole cycles), and the mixer fades a loop in and out, so there is no click at the seam or at a start.

Sound design: dark and grim. Low dull footsteps, leather and steel whooshes, meaty thuds, a
crunch for criticals, formant voices for grunts, yelps, the growl and the howl, a dark bell
swell for level up, and two moody loops (a minor-key drone with sparse plucks for the world, an
organ and choir pad for the menu).
