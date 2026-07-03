Purpose: Record the creation of the project optimization documentation index.

Status: Completed.

Scope: Documentation only; no runtime behavior changed.

## Summary

Added `docs/performance-and-optimizations.md` as the central index for optimization techniques currently supported by the engine. The document consolidates rendering, world geometry, collision, runtime/networking, diagnostics, and asset/resource optimization notes that were previously spread across README, architecture, rendering/assets, and dated ledger records.

The document also records guardrails for future work, including keeping chunk meshes presentation-only, avoiding global static-world cache invalidation on camera movement, keeping occluder checks spatially bounded, avoiding full-world render scans, and keeping texture/resource loading out of per-frame hot paths.

## Verification

- Checked the linked documentation files and references manually.
- Documentation-only change; no build was required.
