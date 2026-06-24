# Collision Alignment And Debug Geometry Planning Log

## Summary

Prepared the design and implementation plan for the next gameplay/debugging pass covering:

- cube visual-vs-physics alignment
- a runtime collision-geometry overlay
- settings-menu integration for that overlay

## Key Evidence Captured

- Bullet cube physics already uses centered `1 x 1 x 1` boxes with zero margin
- `GrassTextureCube.gltf` is already centered and unit-sized
- current render code still applies an extra `(-0.5, -0.5, -0.5)` translation

## Selected Direction

- keep Bullet collision as authoritative
- fix visuals to match collision
- render debug wireframes from Bullet transforms
- expose cubes-plus-player overlay through the runtime settings menu
