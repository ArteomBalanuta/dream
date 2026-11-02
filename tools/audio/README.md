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

File names follow `Dream.Client.Audio.SoundId` in snake_case (`HitCritical` is `hit_critical.wav`).
`build_all.py` fails on clipping, a click at a one-shot's ends, a loop seam jump, a wrong format, or
more than 8 MB in total. Loops are periodic by construction (circular filters and reverb, partials on
whole cycles), and the mixer fades a loop in and out, so there is no click at the seam or at a start.

Sound design: dark and grim. Low dull footsteps, leather and steel whooshes, meaty thuds, a
crunch for criticals, formant voices for grunts, yelps, the growl and the howl, a dark bell
swell for level up, and two moody loops (a minor-key drone with sparse plucks for the world, an
organ and choir pad for the menu).
