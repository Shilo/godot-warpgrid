# Handoff: WarpGrid Phase 5.1 complete — ready for visual validation + next feature

## Session Metadata
- Created: 2026-04-18 21:11:22
- Project: C:\Programming_Files\Shilocity\godot-warpgrid
- Branch: main
- Session duration: multi-hour, spanning Phase 3 finalization → 4 → 5 → 5.1

### Recent Commits (for context)
  - da0c137 feat(phase5.1): skip invisible WarpEffectors in UploadEffectors
  - df42630 feat(phase5): cell-based GridW/GridH + viscosity re-tune
  - c486265 feat(v5): runtime grid resize via property setters + Rebuild()
  - e2b5930 feat(v5): per-cell anchor weighting (RestState.weight)
  - ff29e4d feat(v5): fixed-step accumulator + viscosity calibration + falloff_scale UBO
  - 7960fec docs: spec hygiene - flush stale Phase 3 notes after Phase 4 review
  - f683c2c docs: spec reflects Phase 4 (aspect fix, velocity glow, group-based effectors)
  - 91603d4 docs: add README with tuning guide
  - 799b909 feat: velocity-proportional glow in display shader
  - 128ad01 feat: positions_tex carries velocity in .ba (rgba32f)
  - 261ed27 feat: per-axis spring rest length in compute shader
  - ca1f3ea feat: aspect-corrected effector distance in compute shader
  - 5d48472 feat: add grid_aspect to UBO + bump MaxEffectors to 128
  - a9ef7f2 feat: manager uses warp_effectors group + AABB cull
  - d71fdbb feat: WarpEffector self-registers in warp_effectors group + radius uses min-dim
  - d2b74b6 fix: drop restrict writeonly on binding 1 SSBO (Godot 4.6 reflection bug)

## Handoff Chain

- **Continues from**: None (fresh handoff — consolidates Phase 3-5.1)
- **Supersedes**: None

## Current State Summary

GPU-driven reactive mass-spring WarpGrid prototype in Godot 4.6.2-mono (Windows, D3D12, Forward Mobile). Compute shader runs at fixed 60 Hz via accumulator, writes `(pos, vel)` to an `rgba32f` storage image consumed by a canvas display shader for zero-readback rendering with velocity-proportional glow. Effectors self-register into `"warp_effectors"` group, get AABB-culled, skipped if `!Visible`. Dimensions cell-based (`GridW=10` → 10 cells). Per-cell rest-anchor weights (default 1.0, overridable). Runtime resize via `Rebuild()` triggered by property setters. Anisotropy handled via `grid_aspect` UBO + per-axis spring rest lengths. All 12 Phase 4 + 4 Phase 5 + 1 Phase 5.1 commits green-tested via `mcp__godot__run_project` clean boots. Test scene currently `GridW=36, GridH=20, GridSizePixels=(1152, 648)` = 36×20 cells at 16:9, Effector1 visible, Strength=10.0, position (576,324). Awaiting user visual validation of the snap-and-settle feel.

## Codebase Understanding

## Architecture Overview

Three-layer: (1) **C# driver** `WarpGridManager` owns RenderingDevice resource lifecycle, builds ArrayMesh + MeshInstance2D once, uploads UBO/SSBO each tick, dispatches compute, ping-pongs state buffers A↔B via pre-built uniform sets. (2) **GLSL compute kernel** `WarpGrid.glsl` runs symplectic-Euler with pull-only modified Hooke's 4-neighbor springs, per-cell-weighted rest anchor, effector forces (Radial-Explosive / Radial-Directed / Line-Explosive), boundary anchoring; writes `(new_pos, new_vel)` to storage image. (3) **Canvas shader** `WarpGridDisplay.gdshader` texelFetches position from image per `VERTEX_ID`, brightens line color by `length(velocity) * glow_gain`. `Texture2Drd` bridges the RD-owned image into a standard `ShaderMaterial` slot. Physics deterministic at 60 Hz regardless of engine physics tick rate via accumulator; no GPU→CPU readback.

## Critical Files

| File | Purpose | Relevance |
|------|---------|-----------|
| scripts/WarpGridManager.cs | RD lifecycle, dispatch loop, UBO/SSBO encoding, Rebuild, AABB cull, visibility filter | Primary driver — touch for any system-level change |
| scripts/WarpEffector.cs | Node2D entity, group registration, `ToData` normalizes Radius by min-dim | Per-effector config + GPU-upload marshaling |
| scripts/WarpEffectorData.cs | 32B GPU struct `[Sequential, Pack=1]` + shape/behavior enums | Size-asserted in `_Ready` |
| scripts/MeshHelper.cs | Static grid vertex + line-index generation | Takes node counts (`NodesX, NodesY`), not cell counts |
| shaders/WarpGrid.glsl | Compute kernel (symplectic Euler, springs, effectors, anchor weight) | Only touched by tasks 4+5+7 in Phase 4, + RestState.weight in v5 |
| shaders/WarpGridDisplay.gdshader | Canvas_item shader — vertex fetches from positions_tex, velocity-glow | `glow_gain` default 8, `line_color` Inspector-tunable |
| scenes/warpgrid_test.tscn | Test scene (GridW=36, GridH=20, GridSizePixels=(1152,648), Strength=10.0) | User has been editing dims + strength; respect their current values |
| docs/superpowers/plans/2026-04-18-warpgrid-gpgpu-prototype.md | Phase 3 plan | Historical |
| docs/superpowers/plans/2026-04-18-warpgrid-phase4.md | Phase 4 plan (10 tasks) | Historical |
| docs/superpowers/specs/warpgrid-gpgpu-design-spec.md | Design spec (§15 Phase 4 changelog; still reflects Phase 3/4 — **NOT yet updated for Phase 5 or 5.1**) | Stale regarding v5 items |
| README.md | Tuning guide at repo root | Has Phase 4-era default values in tables (RestDamping=0.10, VelDamp=0.92, glow_gain=60); **stale** vs current Phase 5 tune |

### Key Patterns Discovered

- **Cell vs node**: `GridW`/`GridH` count **cells** since Phase 5. Internal node-count = `NodesX = GridW+1`, `NodesY = GridH+1`. All buffer sizing, mesh gen, shader dispatch extents, texture dims go through those helpers. `grid_spacing = 1/GridW` (since `NodesX-1 == GridW`).
- **RD resource lifecycle**: RIDs owned by manager. `_ExitTree` calls `TeardownGpu()` (frees 10 RIDs, nulls each field, sets `_rd = null`). `Rebuild()` = teardown + Free MeshInstance + BuildMesh + InitGpu + BuildMaterial. `MaybeRebuild()` guard = `_rd != null` gate so scene-load property setters (pre-`_Ready`) don't recurse.
- **std140 UBO = 64 bytes**: Order-critical. `_pad0` at offset 52 was replaced by `falloff_scale` in v5 (still 64 B total; `grid_aspect` at 56).
- **std430 RestState stride = 16 bytes** (v5): `{vec2 anchor, float weight, float _pad}`. C# `RestStride=16` must match.
- **Godot 4.6.2 SPIR-V reflection bug**: `restrict writeonly buffer` on SSBO bindings misclassifies them as textures. Fix in `d2b74b6` — bindings 0+1 are plain `buffer` (no qualifiers). Do NOT re-add `writeonly` there.
- **C# build gate**: shader changes auto-reimported by editor, but **C# changes require `dotnet build "Godot WarpGrid Test.csproj"` before `mcp__godot__run_project`**. Otherwise runtime loads stale DLL. Silent failure mode — code "works" but uses old behavior.
- **`.godot/imported/` is sacred**: binary compiled artifacts (shader `.res`, texture `.ctex`). Only the editor can regenerate them. Runtime (non-editor mode) cannot. Never `rm` anything from here — relaunch the editor via `mcp__godot__launch_editor` to trigger reimport.
- **Scene auto-strips defaults**: Godot editor strips property lines matching class defaults on save. Absence of a line in `.tscn` ≠ missing config; it means "use default".
- **Effector visibility**: Phase 5.1 adds `!eff.Visible` check in UploadEffectors. Hidden effector → no GPU upload → zero warp contribution.

## Work Completed

### Tasks Finished

- [x] Phase 3 code calibration committed + docs synced (0+ predecessor phases already shipped)
- [x] d2b74b6 — binding-1 SPIR-V reflection fix
- [x] Phase 4 full plan execution (10 tasks, 12 commits) — group-based effectors, AABB cull, grid_aspect, per-axis springs, rgba32f velocity channel, velocity-glow display shader, MaxEffectors 32→128, README + spec
- [x] Phase 4 cross-cutting code review + §12/§14 spec hygiene pass
- [x] v5 wave 1 — fixed-step accumulator, viscosity calibration (RestDamping/VelDamp/FalloffScale), glow_gain 60→20
- [x] v5 wave 2 — per-cell RestState.weight, shader multiplier, `RestAnchorWeight(x, y)` virtual hook
- [x] v5 wave 3 — property-backed GridW/H/GridSizePixels setters + `Rebuild()` + `TeardownGpu()` extraction
- [x] Phase 5 cell-based dimensions refactor (NodesX/Y helpers, MeshHelper + InitGpu + UBO + dispatch all routed)
- [x] Phase 5 re-tune — RestDamping 0.4→1.2, VelDamp 0.88→0.85, FalloffScale 1000→500, glow_gain 20→8
- [x] Phase 5.1 effector visibility gate

## Files Modified

| File | Changes | Rationale |
|------|---------|-----------|
| scripts/WarpGridManager.cs | Property setters, accumulator, NodesX/Y helpers, RestState writes, visibility filter, all constants retuned | Driver absorbs most system-level changes |
| scripts/WarpEffector.cs | Group self-registration, Radius/min-dim normalization | Decouple from manager NodePath wiring |
| scripts/WarpEffectorData.cs | (unchanged since Phase 3) | Size-stable 32B blittable |
| scripts/MeshHelper.cs | (unchanged) | API was already node-count based |
| shaders/WarpGrid.glsl | UBO adds falloff_scale + grid_aspect, RestState.weight + _pad, effector uses d_raw vs d, per-axis rest_len, rgba32f image, bindings 0/1 no qualifiers | Physics math + GPU-side struct evolution |
| shaders/WarpGridDisplay.gdshader | rgba32f sampler, velocity glow mix, glow_gain=8 | Renderer-side visual polish |
| scenes/warpgrid_test.tscn | GridW=36, GridH=20, GridSizePixels=(1152,648), Strength=10.0 | User-driven tuning |
| README.md | Tuning guide (Phase 4 snapshot) | Onboarding docs — **stale re: Phase 5** |
| docs/superpowers/specs/warpgrid-gpgpu-design-spec.md | Full Phase 4 reflection incl. §15 changelog | **Stale re: Phase 5 + 5.1** |
| docs/superpowers/plans/2026-04-18-warpgrid-phase4.md | 10-task plan | Historical |

## Decisions Made

| Decision | Options Considered | Rationale |
|----------|-------------------|-----------|
| Property setters for GridW/H/GridSizePixels vs explicit `Rebuild()` only | Setter-auto-rebuild vs manual method | Setter gives Inspector-live resize; `_rd != null` guard keeps scene-load safe |
| Cell count semantic for GridW | (a) keep node-count, (b) switch to cell-count | (b) — matches user intuition ("GridW=10 means 10 cells across") |
| `d_raw` vs `d` in effector_force | Single vector with mixed semantics | Two vectors — `d` aspect-corrected for radius/falloff, `d_raw` for push direction so forces act along geometric line |
| `RestState { vec2 anchor, float weight, float _pad }` vs `vec4 anchor_weight` | Layout style | Named fields clearer; std430 stride rounds to 16 either way |
| Skip `writing-plans` skill for small patches (v5, Phase 5, Phase 5.1) | Plan everything via subagent-driven-development vs inline | Scope small enough; inline execution with per-step build+verify cheaper |
| Phase 5 spec/README updates deferred | Update on every phase vs batch later | Docs stale — user-facing defaults in README table are Phase 4 values; spec §15 is Phase 4 changelog only |

## Pending Work

## Immediate Next Steps

1. **User visual validation** of Phase 5.1 on current scene (GridW=36, GridH=20, Effector1 Strength=10.0). Expected: exactly 36 visible cells horizontally + 20 vertically, snap-and-settle in ~0.5 s on impact, effector visibility toggle flattens/restores grid instantly. User has been tuning scene values live.
2. **Docs refresh** — README tuning table still shows Phase 4 values (RestDamping=0.10, VelDamp=0.92, glow_gain=60). Needs Phase 5 numbers. Design spec §15 is Phase 4 changelog — append §16 Phase 5 / §17 Phase 5.1 summaries, patch UBO table (falloff_scale), update RestState struct docs, document NodesX/Y cell-count semantic, document `RestAnchorWeight` hook, document `Rebuild()` public method.
3. **Phase 6 candidates** (from spec §14 as now renumbered): dynamic `dt` injection (pass real delta instead of fixed 1/60), multi-grid instances, effector cap > 128 via indirect dispatch. User will direct.

### Blockers/Open Questions

- [ ] None currently blocking. Docs drift is a known tech-debt item but doesn't gate runtime.

### Deferred Items

- **Dynamic `dt`** — accumulator locks shader at exact 1/60. Allowing runtime `PhysicsTicksPerSecond` variation would need to feed `delta` into UBO each tick instead of the constant `Dt = 1.0f/60.0f`. Not urgent while the fixed-step feel is what the user wants.
- **Per-effector behavior=Impulse path live-test** — ImpulseCap=0.5 wired up but not exercised; user has only played with Force behavior at Strength 0.01 → 10.0 scale.
- **RenderDoc capture** — never needed; all boots clean on NVIDIA RTX 3070 Ti / D3D12 12_0 Forward Mobile.
- **Monorepo/library extraction** — user explicitly deferred multiple times ("We are ignoring monorepo structure for now. Focus exclusively on logic, stability, and gameplay features within the Godot prototype.").

## Context for Resuming Agent

## Important Context

**MUST know before editing C#**: rebuild via `dotnet build "Godot WarpGrid Test.csproj"` from project dir before `mcp__godot__run_project`. Silent staleness if you skip. Check DLL timestamp at `.godot/mono/temp/bin/Debug/Godot WarpGrid Test.dll` vs source if unsure.

**MUST know before editing shaders**: Godot editor auto-reimports on save. If you manually delete `.godot/imported/WarpGrid.glsl-*.res` (don't), runtime can't regenerate — only the editor can. Recover with `mcp__godot__launch_editor`.

**MUST know about SPIR-V reflection**: Godot 4.6.2 misclassifies `restrict writeonly buffer` at binding 0 or 1 as a texture → `uniform_set_create` fails with "Texture (binding: 1, index 0) is not a valid texture." Bindings 0+1 in WarpGrid.glsl MUST stay plain `buffer`. Other qualifier combinations (restrict-only on binding 2/3, writeonly on the `image2D` at binding 5) are fine.

**MUST know about the UBO**: exactly 64 bytes std140. C# `UploadParams` writes 16 × 4 B = 64 B in strict order. Any offset drift silently corrupts `grid_aspect` reads (subtle anisotropy bug) or `falloff_scale` reads (wrong effector feel). If you add a new field, update BOTH GLSL block AND C# writer together, respecting std140 alignment rules (vec2 needs 8-byte alignment).

**MUST know about RestStride=16**: std430 rounds the `{vec2, float, float}` struct to 16 bytes. C# `RestStride = 16`. Init loop writes 4 floats per node: `nx, ny, weight, 0.0f`. Per-cell weight overridable via `protected virtual float RestAnchorWeight(int x, int y) => 1.0f;` — subclass WarpGridManager + `Rebuild()` to apply softness maps.

**Cell-count semantic** (Phase 5): `GridW=10` means 10 cells, which internally produces 11 vertices per axis. `NodesX = GridW + 1`. Anyone writing new code against this project must route node counts through `NodesX`/`NodesY`, not `GridW`/`GridH`.

**Visibility filter** (Phase 5.1): `UploadEffectors` skips `!eff.Visible`. Effector with `visible = false` exerts zero force. Entire subtree hidden (e.g., parent toggled) also applies via Godot's `Visible` inheritance (actually Godot's `Visible` only reflects the node's own flag, not ancestors — use `IsVisibleInTree()` for full hierarchy. Current impl uses `.Visible`, matching the explicit Phase 5.1 instruction).

## Assumptions Made

- Main RenderingDevice (not local) sharing texture with canvas renderer via Texture2DRD bridge.
- D3D12 Forward Mobile (user's setup) — also works on Forward+ but not tested.
- Max 128 effectors. SSBO = 4 KB, fine.
- All effectors are Node2D (not 3D or UI nodes). Group fetch is generic, cast gate filters.
- User is running on NVIDIA RTX 3070 Ti (fast compute, 10k nodes trivial). If porting to low-end hardware, 160×90 × rgba32f image = 56 KB is still trivial but physics tick cost may need profiling.
- "Phase" naming is user-driven organizational, not strict SDLC. Phase N+1 can start before N's docs are written.

## Potential Gotchas

- **`eff.Visible` vs `eff.IsVisibleInTree()`**: Current Phase 5.1 check uses `Visible` (node's own flag). If user hides a parent, the effector node's own `Visible` may still be true, so it'd still contribute. If user wants hierarchy-aware hiding, swap to `IsVisibleInTree()`. Worth flagging if they say "effector hidden via parent toggle doesn't flatten grid."
- **Scene file auto-strips**: editor silently drops properties matching class defaults. Don't over-read diffs: `Strength = 0.01` disappearing usually means "still 0.01, just equals current default". If the user's default changed, it may look like a regression.
- **Property setter recursion**: `GridSizePixels = value` inside `Rebuild()` could re-trigger `MaybeRebuild()` if we weren't careful. Guard is `if (_gridW == value) return;` + `_rd != null`. Safe as-is but watch out if adding new properties that mirror.
- **`Rebuild()` during _PhysicsProcess**: user-script calls to `Rebuild()` from `_PhysicsProcess` handler would tear down RIDs mid-frame — not currently guarded. Don't do it. Rebuild from `_Process`, UI callbacks, or timers.
- **Accumulator catch-up cap = 8 steps**: `maxAccum = Dt * 8 = 0.1333 s`. On a multi-second frame spike, the physics "time-travels forward" by at most 0.13 s. Intentional (avoids spiral-of-death) but means after a hitch, grid won't "catch up" fully — fine for a visual layer, not fine for a physics-coupled gameplay grid.
- **`glow_gain=8` is tuned for v5 stiffness/damping**. If you halve damping, residual motion will feel over-glowed again. Re-tune glow to ~15 for looser physics.

## Environment State

### Tools/Services Used

- Godot 4.6.2-mono (Windows, D3D12, Forward Mobile renderer) — launched via `mcp__godot__launch_editor` / `mcp__godot__run_project`
- `dotnet build` for C# assembly (.NET 8 SDK required)
- MCP servers: `godot` (project lifecycle), `context7` (library docs; rarely used — math/logic work not its domain)
- superpowers plugin skills: `writing-plans`, `subagent-driven-development`, `brainstorming` (skipped for inline patches)

### Active Processes

- None running (last `mcp__godot__stop_project` call in session was clean).
- Godot editor likely still open in background from the `launch_editor` call during Phase 5.1 troubleshooting; harmless.

### Environment Variables

- No project-specific env vars set. Godot reads from project.godot.

## Related Resources

- README.md — tuning guide (Phase 4 era values, needs Phase 5 update)
- docs/superpowers/specs/warpgrid-gpgpu-design-spec.md — canonical design (§15 is Phase 4 changelog)
- docs/superpowers/plans/2026-04-18-warpgrid-gpgpu-prototype.md — Phase 3 plan
- docs/superpowers/plans/2026-04-18-warpgrid-phase4.md — Phase 4 plan
- Reference: Geometry Wars: Retro Evolved aesthetic — taut elastic membrane, pull-only springs, ripple reflections off anchored perimeter
- Reference: XNA "Neon Vector Shooter" tutorial (cited by user in earlier context)

---

**Security Reminder**: No secrets present. Validation pending.
