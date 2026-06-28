# Floating Combat Text System Specification
## MU Online Inspired Damage Display Framework

This document outlines the complete structural, visual, and behavioral specification required to implement a high-impact Floating Combat Text (FCT) system modeled after the iconic display aesthetics of *MU Online*.

---

## 1. System Overview & Core Philosophy
In traditional ARPGs and MMORPGs, numbers tell a story. MU Online’s system transforms raw numbers into an instant dopamine hit and cognitive cue through deliberate color choices, size hierarchies, and mechanical weight. 

An effective implementation must ensure that a player can instantly recognize a rare combat modifier (such as *Excellent* or *Ignore Defense* damage) out of the corner of their eye without reading the exact numerical digits.

---

## 2. The Color & Typography Matrix
Every point of damage must be processed through a central evaluation pipe (`CombatTextManager`) which inspects the attributes of the strike and maps it to a unique style package.

### Visual Attribute Breakdown

| Damage Classification | Base Color (Hex) | Outer Glow / Stroke | Font Scale Modifier | Depth Priority (Z-Index) | Architectural Trigger / Calculation |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Regular Damage** | Orange (`#FF6600`) | 1px Black Stroke | `1.0x` (Base) | Baseline (10) | `Final_Damage = Attacker_Power - Target_Defense` |
| **Critical Damage** | Electric Blue (`#0066FF`) | 2px Blue-Shift Glow | `1.15x` | Elevated (20) | Triggered via Crit Rate % success. Forces maximum possible value within the base damage range. |
| **Excellent Damage** | Neon Green (`#00FF00`) | 3px Bright Green Aura | `1.25x` | High (30) | Triggered via Excellent Strike % check. Returns `Max_Damage * 1.10`. |
| **Ignore Defense** | Cyan / Turquoise (`#00FFFF`) | 2px Cyan Shimmer | `1.20x` | High (35) | Triggered by Ignore Defense rate. Bypasses target defense armor calculation entirely (`Target_Defense = 0`). |
| **Incoming Damage** | Crimson Red (`#FF0000`) | 1px Heavy Drop Shadow | `1.0x` | Foreground (50) | Any damage source successfully applied directly to the player's primary HP pool. |
| **Reflect Damage** | Purple (`#800080`) | Minimal Drop Shadow | `0.85x` | Low (5) | Triggered when an entity hits a target possessing reflection enchantments. Returns an isolated fraction back to attacker. |
| **SD Absorbed (PvP)** | Olive / Gold (`#8B8000`) | 1px Dark Outline | `0.95x` | Baseline (12) | Portion of incoming PvP damage mitigated by Shield Defense layer before health depletion begins. |
| **Poison / DoT Ticks** | Dull Olive Green (`#556B2F`)| None | `0.80x` | Background (1) | Direct damage-over-time tick induced by status ailments, skipping standard hit physics. |

### Font Style Guidelines
* **Typography Choice:** Heavy, geometric, compact sans-serif/semi-serif typeface (e.g., *Impact*, *Arial Black*, or custom blocky medieval/futuristic fonts). Avoid delicate serifs or thin script faces.
* **The Double-Outline Rule:** Text must remain legible over chaotic 3D backdrops (e.g., flashy particle effects, dark lava zones, bright snowy fields). Apply an explicit 1-to-2-pixel solid black outline, or a hard-edged, zero-blur drop shadow shifted exactly 1.5 units down and right.
* **Kerning:** Compress letter-spacing by roughly `-5%` to `-10%`. Numbers must feel like single cohesive tokens or stamps, rather than spaced-out individual text letters.

---

## 3. Kinematics, Physics & Lifecycle
Text should never simply clip onto the screen and float straight upward at a static speed. It requires physical impact.

```
   (Y-Axis)
      ^
      |       [Peak Height / Full Fade] (0.8s - 1.2s) - Alpha -> 0
      |          .
      |         .  <- Slower upward crawl (Ease-Out Quad)
      |        .
      |       /
      |     o    <- Scale reaches maximum size (0.15s)
      |    /
      |  /   <- Explosive initial leap / burst upward
      | X    <- Spawn Target Pivot (0.0s) + Small Random X-Offset
      +--------------------------------------------------------> (X-Axis)
```

### The Three-Phase Lifecycle

#### Phase 1: The Burst (0.0s – 0.15s)
* **Scale Jump:** On initialization, the numbers instantiate at a microscopic scale (e.g., `0.2`) and violently scale up past their normal target bounds to `1.1x` before settling down to their native scale modifier by `0.15s`. This simulates an explosion coming from inside the enemy.
* **Spatial Offsetting:** Every time text spawns, add a random minor horizontal offset along the local X-axis ($\pm 0.35$ units relative to target center) so overlapping numbers don't overlay each other perfectly.

#### Phase 2: The Decelerating Float (0.15s – 0.60s)
* **Velocity Decay:** Use an explicit **Ease-Out Quad** interpolation function for vertical positioning. The text should fly upward very quickly immediately following its birth, then gradually slow its accent to a gentle, nearly stationary hover near the peak of its flight path.
* **Formula Example:**
  $$	ext{Current\_Y} = 	ext{Spawn\_Y} + (	ext{Max\_Height} 	imes (1 - (1 - t)^2))$$
  *(Where $t$ represents elapsed time normalized between 0.0 and 1.0 during this phase)*

#### Phase 3: The Dissolve & Cleanup (0.60s – 1.0s)
* **Alpha Decay:** Linearly fade the alpha transparency channel (`1.0` down to `0.0`) over the final 400 milliseconds.
* **Garbage Cleanup:** The microsecond alpha hits `0.0`, the text container is disabled and returned to the system object pool. Do not call standard engine destroy features mid-combat.

---

## 4. Technical Architecture & Constraints

To deploy this in a production-ready engine (Unity, Unreal Engine, Godot, or custom C++ frameworks) without introducing garbage collection stalls or rendering hitches, adhere to the following architecture rules:

### A. High-Performance Object Pooling
Spawning heap objects dynamically for every hit in a high-density area or multi-hit AoE skill will trigger immediate frame rate drops.
* Pre-warm a static list/array containing **150 to 300 Text Mesh Objects** at the start of a map load.
* When damage is dealt, request an inactive instance from the manager pool, initialize its position, reset its alpha/scale states, and set it to active.

### B. Canvas Screen Positioning
* **Do Not** use 3D World Canvases pinned inside the game world coordinates where camera rotation will warp the text face away from the player.
* **Do** use a 2D Screen Space Overlay layer. Compute the target's 3D head coordinates inside the world space, convert them to 2D screen positions via your active camera matrix (`WorldToScreenPoint`), and overlay the text elements exactly at those coordinates.

### C. Accumulation & Overflow Optimization
When an asset/character applies a hyper-fast multi-hit skill (e.g., 10 strikes inside a 100ms window), rendering 10 independent numbers directly on top of each other creates an illegible blob.
* **Culling Threshold:** Restrict any single monster target to a maximum of **4 active floating text modules** concurrently.
* **Text Merging (Optional alternative):** If a hit occurs while a previous number of the *same type* is inside Phase 1 or 2 of its life cycle, instead of spawning a new object, add the values together, update the existing display text string, and reset its visual scale pop back to `1.1x` to indicate a combined continuous combo.