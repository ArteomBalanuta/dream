Purpose: Summarize the remaining combat/world roadmap after the first player combat, destructible-world, and HUD slices landed.
Status: Analysis
Scope: Roadmap decomposition for the next combat/world phases

# Summary

The earlier combat/world roadmap is no longer one large unfinished block. The remaining work now falls into four distinct tracks that can be planned and executed independently:

1. enemy runtime
2. broader world-object expansion
3. wider event expansion
4. future stat/modifier framework

Splitting the roadmap this way keeps the project aligned with the current architecture:

- `PlayScene` owns scene orchestration
- combat capability remains interface- and adapter-driven
- scene-local events stay focused on facts and reactions
- systems grow from real gameplay needs instead of speculative engine layers

# Recommended Order

## Near-Term

1. `enemy runtime`
2. `world-object expansion`

## Mid-Term

1. `event expansion`
2. `future stat/modifier framework`

# Why This Order

The project now has enough combat infrastructure to support a real enemy actor. That is the highest-value missing gameplay slice because it validates:

- actor-vs-actor combat
- non-player death cleanup
- future hostile behavior hooks

World-object expansion follows naturally because the generic registry exists, but production use is still mostly cube-centric.

Event expansion should continue after those gameplay slices produce more real facts worth subscribing to. The event model is already healthy enough for the current stage and does not need speculative broadening first.

The full stat/modifier framework should remain the most deferred track. The project now has a useful player stat slice, but not yet enough actor, loot, equipment, or buff/debuff depth to justify a generalized modifier matrix.

# Linked Roadmaps

- [Enemy Runtime Roadmap](./2026-06-28-enemy-runtime-roadmap.md)
- [World-Object Expansion Roadmap](./2026-06-28-world-object-expansion-roadmap.md)
- [Event Expansion Roadmap](./2026-06-28-event-expansion-roadmap.md)
- [Stat Modifier Framework Roadmap](./2026-06-28-stat-modifier-framework-roadmap.md)
