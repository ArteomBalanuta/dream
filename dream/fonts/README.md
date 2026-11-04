# Fonts

These two typefaces are **third-party art**, not original to this project. They were added deliberately
(the project owner chose a vendored open-licence face for the HUD) and are the only third-party fonts here.

| File | Face | Used for | Licence | Source |
|---|---|---|---|---|
| `Cinzel.ttf` | Cinzel (variable, `wght` 400-900) | `UiTextStyle.Large`: status numbers, window titles | SIL Open Font License 1.1, `Cinzel-OFL.txt`, "Copyright 2020 The Cinzel Project Authors" | https://github.com/NDISCOVER/Cinzel, taken from the Google Fonts mirror https://github.com/google/fonts (`ofl/cinzel`) |
| `EBGaramond.ttf` | EB Garamond (variable, `wght` 400-800) | `UiTextStyle.Serif`: the character (stats) window | SIL Open Font License 1.1, `EBGaramond-OFL.txt`, "Copyright 2017 The EB Garamond Project Authors" | https://github.com/octaviopardo/EBGaramond12, taken from https://github.com/google/fonts (`ofl/ebgaramond`) |

The HUD's small text (`UiTextStyle.Body`, 13 px) is ImGui's built-in ProggyClean (MIT, part of Dear ImGui).

The OFL permits bundling and redistribution with software; the fonts are not renamed or modified, and the
licence texts travel with them. Both files are variable fonts. ImGui's stb_truetype backend ignores the
weight axis and renders the default instance (Regular, 400). Which face goes where is decided in
`dream/Rendering/Ui/UiFontSetup.cs`; a missing file falls back to the built-in face.

`Arial.ttf` used to be here. It is a proprietary Microsoft font with no licence file and nothing loaded it,
so it was removed.
