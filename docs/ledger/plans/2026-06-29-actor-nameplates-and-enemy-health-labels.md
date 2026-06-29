# Actor Nameplates And Enemy Health Labels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replicate actor display names through authoritative snapshots and render always-on world labels for remote players and enemies, including wolf HP.

**Architecture:** Extend `ActorSnapshot` with optional display metadata, keep player names authoritative in the server-side session registry, and let client presentation build nameplate state from replicated snapshots plus authoritative enemy/runtime health. Render a lightweight always-on world-label overlay from the existing HUD projection path so labels remain client-local presentation while all text content stays server-authored.

**Tech Stack:** C# / .NET 10, OpenTK, ImGui.NET, existing multiplayer snapshot DTOs, existing `Render` HUD projection path

---
