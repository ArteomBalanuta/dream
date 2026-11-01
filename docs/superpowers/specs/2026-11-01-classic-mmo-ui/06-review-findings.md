# 06: Review findings against the pixels

Independent adversarial review of specs 01, 02, 03, 05 (re-measured with the pixel probe) and contradictions with 04 and `DMG_IMPL.md`. Hand-counted tally: 176 claims checked, 39 wrong (01: 10/38, 02: 6/24, 03: 12/34, 05: 8/20, on-screen text: 3/55, 04 citations: 0/5). Status of each fix is at the end of this file.

Scope tags: [01] [02] [03] [04] [05] name the spec that must change.

## Must fix

1. [03] Inventory left edge and image 3 scale (lines 19, 182, 239). Cream rim is at x=1271-1272 in both 2.webp (y=60, `#F8F0DA`) and 3.webp (y=200, `#B9B2A2`); x 1280-1281 is the chain column. Correct: inventory x 1270-1803 = 533 px = 189 u, 29.6% W. Images 2 and 3 share x origin and scale 1.00; remove the 0.99 and the 1821 frame width. Screen centre 901.5; notice centres 905, 902, 901 then fit directly. 01's x=450 u is confirmed.
2. [03] Section 8 and layout table: x 1265-1267 (`#DCC9A6`) is the chat post, 1271-1273 is the inventory rim. Visible chat ends at about x 1268; anything further is hidden under the inventory; mirroring the left post predicts about 1277 (INFERRED).
3. [03] "Improper items for combination" is not a notice. It belongs to the combine panel (x=44, aligned with `Combining Success rate`). Delete from 03 section 16 and delete `notice.error`; "up to 5 lines" means 5 gold lines. [01] keep `text.error`, tag `#F46650` as a peak (top-10% saturated mean `#C64230`).
4. [03] Notice flow. Replace "starts high and grows down" and close open question 8. Positions converted to img5 y: BK/MG f3 251, f5 138, f7 64, f10 gone; /offattack f5 212, f7 137, f10 100; For free f5 249, f7 173, f10 137. All sit on a 37 px lattice 64, 101, 138, 175, 212, 249, with blank lines possible (175 empty in img5). Lines enter around y=250 in img5 px (about 89 u from the top), step up one pitch at a time, and leave at the top; the list scrolls with no new line pushing it, so it is time-stepped. Frame order is assumed; rate unknown.
5. [03] Notice band opacity: transmission 0.48 (img3 x=1000, `#554618` to `#29200C`) and 0.55 (img5 x=200, `#45474D` to `#26262A`). `notice.gold` band black about 50% (45-52%).
6. [01] Tooltip placement (line 300): 02 wins. "Flush above" is false: footprint y 311-423, panel bottom 340, overlap 29 px, about 0.5 c (6.png; 7.png same). img10 footprint bottom 454-455, panel top 450-452. Panel x 110-509, centre 309.5, footprint centre 311. Delete the "clamped to screen" evidence (6.png is a crop). Rule: above, panel bottom = footprint top + 0.5 c; below, panel top = footprint bottom. [02] note the overlap has only two samples.
7. [02] Section 7 jewel identities: amber gem = Jewel of Chaos (INFERRED, strong: img5 requires a Jewel of Chaos and img10's valid 90% recipe shows wings, axe, feather, amber gem and no disc; combine row 4 is hidden). Disc is unidentified.
8. [01 02] Zen and spear: img4 to img5 removes gold helm, green pants and one amber gem, adds the red spear, and Zen drops by exactly 1,000,000. img3 shows `You have successfully created the item`. INFERRED: the spear is a combination output paid for by that Zen. Remove "cause unknown" (01 section 4 and section 9 item 9, 02 open questions 2 and 5).
9. [05] Blend: use `rgb = tex.rgb * tint * fade` so fading stays non-additive.
10. [05] Rim: 25% grey times tint cannot produce the table's rims, and measured rims are not a multiply. Use a two-channel atlas (R fill mask, G rim mask) with per-type `fill` and `rim` uniforms.
11. [05] Pseudo-code: add `n.screenY = anchor.y - n.y` before the guard; state 12 as a soft cap or remove rows at cap; define `merged` as the newest same-type number in that tick.
12. [05] 1080p sizes (line 264): 1.webp is a crop of unknown scale. If `Miss` uses the UI font, img1 is about 1.4x img2, giving about 8 px floor and 23 px spawn at 1080p. Tag 24/68 as a design choice; only the ratio 2.7-2.9:1 is evidence. Use 03's scale rule.
13. [05] Miss fixed scale 0.47 (0.38 used the wrong denominator).
14. [05 03] Fourth family: C1 (x 266-330, y 214-226) top-10% `#1FD3EE`, median hue 186; blues are hue 205-208. Add cyan to 05 tokens as `dmg.ignore`, remove "?1758 as outlier" from the case against H1, tally blue 7, cyan 1. 03's coordinate (240,290) is a wrong citation.
15. [03] 05 owns combat text. Replace 03 section 15, the `dmg.*` tokens and the "Numbers" state row with a pointer to 05, keeping cyan. Verdicts: green 05's `#15E8B5`; gold 05's `#FDB442` (measured `#F9A71C`, `#F6A726`, B channel high); two columns at x centres 203, 303; Miss pitch 21-22; readings 05's. [05] drop `106252 = 2 x 53126` as support (line 207, H1): only the top half of those digits is visible.
16. [05] `DMG_IMPL.md` vs pixels: pixels win on look; hue-to-meaning agrees (regular gold/orange, crit blue, excellent green, ignore cyan). Add a section recording: hex from 05 plus `#1FD3EE`; no glow, hue-dark rim; starts large and shrinks to 0.36, not 0.2-1.1-settle; per-type scale from 05; newest on top; monospaced with advance = fill height, not negative kerning; cap 12, not 4; excellent x1.20 (53126/44284 = 1.1997), not x1.10; merge only past cap; lifetime has no evidence, keep 1.4 s tunable (existing code about 1.67 s). Add Incoming, Reflect, SD, Poison rows as placeholders with DMG_IMPL hues and a hue-dark rim.
17. [02 04] Tier colours: 02 has Uncommon blue, Rare green; code has Uncommon green, Rare blue. 02 wins for the reskin, but Uncommon in blue collides with `tip.text.bonus`: pick a non-bonus Uncommon colour. Add the recolour to 04.
18. [02 04] Dependencies: no FBO, and `UserCallback` is skipped (`Render.ImGui.cs:295`). Add to 04's gap table: FBO bake tool, custom UI shader path, nearest-filter upload. 02 must link to them.

## Should fix

19. [05] Line 167: hits are bursty, not near-uniform (233 to 263 and 175 to 214 gaps). Relabel the curve as suggested, with no fit claim.
20. [05] Size tables: one column, fill/40.
21. [05] Line 77: left bar y 243-250 is dark (`#261202`-`#3F181A`). Right bar y 190-200 is a dark-outlined rectangle with red-orange interior (`#A03D18`, `#D9642C`), possibly an HP fill.
22. [01] Line 191: a 2x3 item shows 3 interior lattice lines (1 vertical, 2 horizontal).
23. [01 02] Cell tokens: `cell.hover` `#1C2030`; `cell.tile` `#404143`; `cell.occupied` `#232425`; normal lattice vertical `#444648`-`#4F4F4F`, horizontal `#303234`; hover lattice vertical `#323848`, horizontal `#262939`.
24. [01 02] Armour overflow: 11 px, about 4 u, 0.2 c (white extent x=28 img4, x=557 img5; grid left 39, 568).
25. [02] Tooltip top pad 21 px, 0.37 c; bottom pad 24-28 px; panel width in img10 is 400.
26. Rendering conflicts. Scale: 0.25 snap wins; [01] line 318 changes (1080p needs 2.25 to fill 480 u). Text: [01]'s rule wins: text drawn at screen px, not scaled with art; [02 03] align; one font rule lives in 00-README. Units: 1 c = 20 u, H = 48 u; at 2.817 px/u image 2's bar spans u 433-480, corroborating 432 u and the letterbox reading. Units table lives in 00-README.
27. [01] Line 305: axe colour; point to 02 section 9 (same bag slot, stable controls).
28. [03 02 05] SD on a Season 2 client: keep neutral `gauge.left` and `gauge.right`; SD/AG are dream design labels. Rename `hud.sd.*`, `hud.ag.*`.
29. [03] Line 182: Repair and Store also vanish while Combine is open.
30. [01 02] Scale fits: img11 scale 2.00, pitch 39.9-40.0; img10 pitch 56.3-56.5; 02's lines at x 30, 86, 142, 198 imply 56.0; measured peaks 81.5, 137.5, 193.5, 250.5.

## Over-tagged claims

- [02] Line 97 and table line 222: cursor above tooltip: INFERRED.
- [01] Section 5: slot height 432: INFERRED (supported by finding 26). Line 30: remove "190 u x 2.816 = 535, the image width", a coincidence. Section 3.11: button functions INFERRED. Section 7: "likely a system UI face" INFERRED. Line 167: cape overflow bottom is about 1.5-2 u.
- [05] Line 107 Fade: INFERRED. Line 7 "hidden target zone": INFERRED. Line 135: 7 x steps listed for 6 y values.
- [03] Line 46: dragon eye peak `#BDA884` at (168,96). Line 60: coordinates plate is a gradient, `#000000` to `#282B2A`.

## Gaps

- [03] Actor HP and name bars over monsters are unspecified. Describe what the frames show, including finding 21.
- [01] Black hollow cone in 1.webp (x 215-270, y 440-495): unidentified, possibly an attack cursor.
- [04] No gap row for cursor, notices, combine panel, coordinates plate, durability, requirement data.
- [03] Bar's top gold rail outside the gauges (about y 152 near x 480-530, x 1330-1540). Section 8: JUIZ's line has the gold bar too, not only BayE's.
- [01] 11.webp right of the rim (x 383-399): black with a tan oval and white specks, unexplained.
- [04] `DMG_IMPL.md` table runs to line 29 (Poison).
- Zen's last three digits are unreadable: `705` is a guess; only 745/744 is legible.

## Fix status

One fix loop was run. Every finding was applied, except:

- 25, partly: 02 keeps top pad 17-21 px, because 6.png and 9.png measure 17-18.
- 8, partly: 01 and 02 say a drop of 1,000,000 only if the unreadable last digits are unchanged.
- 28: no change needed in 02, which has no SD or AG.

Additions from re-measuring: 03 measures the chat right post at x 1277-1279 at input height (y 110-140), which supersedes finding 2's prediction; 01 notes img10's vertical pitch as 56.7.

After the loop, a text search confirmed the corrected stale values are gone. The specs were not given a second full pixel review.
