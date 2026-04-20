# GPU PBD Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current scalar wave solver with a multi-dispatch GPU position-based dynamics solver that keeps a low-resolution physics grid, preserves the high-resolution visual mesh, and produces localized, stiff, arcade-like dents with stable snap-back.

**Architecture:** `WarpGridManager` will upload a PBD parameter block and dispatch the compute kernel multiple times per frame, ping-ponging `NodeState` buffers between prediction, solver, and final integration passes. The compute shader will store current/previous positions in pixel space, enforce anchor and neighbor constraints directly, optionally hard-pin the one-cell border, and write current/previous positions to the display texture so the canvas shader can derive velocity glow from displacement.

**Tech Stack:** Godot 4.6.2-mono, C#/.NET 8, `RenderingDevice`, GLSL compute shader, `canvas_item` shader, `dotnet build`.

---

### Task 1: Lock the PBD data contract

**Files:**
- Modify: `scripts/WarpGridManager.cs`
- Modify: `shaders/WarpGrid.glsl`
- Modify: `shaders/WarpGridDisplay.gdshader`

- [ ] Define `NodeState` as `vec2 current` + `vec2 prev` in the compute shader and mirror that contract in manager scratch-buffer initialization comments/constants.
- [ ] Replace wave-equation UBO fields with PBD fields for grid size, spacing, solver iteration flags, stiffnesses, damping/friction, effector count, force scaler, and boundary pinning.
- [ ] Keep the display texture as `rgba32f`, but change the semantic contract to `.xy = current_pos`, `.zw = prev_pos`.
- [ ] Run: `dotnet build "Godot WarpGrid Test.sln"`
- [ ] Expected: build succeeds with the updated manager-side constants and any shader resource-loading code still compiling.

### Task 2: Add manager-side exported controls and multi-dispatch flow

**Files:**
- Modify: `scripts/WarpGridManager.cs`

- [ ] Add exported properties with `MaybeUploadParams()` setters for `SolverIterations`, `NeighborStiffness`, `AnchorStiffness`, `GlobalDamping`, `Friction`, and `BoundaryPinning`.
- [ ] Preserve the decoupled physics/visual grid setup and `_isInitialized` lifecycle guard.
- [ ] Change `_PhysicsProcess` so effectors are uploaded once per engine frame, params are uploaded once per engine frame, and the compute dispatch runs `SubSteps * SolverIterations` times with correct ping-pong swapping between iterations.
- [ ] Ensure force injection is normalized to the first solver iteration of each substep so increasing `SolverIterations` does not multiply effector energy.
- [ ] After the dispatch loop, call `_rd.Submit()` and `_rd.Sync()` so the display texture matches the latest projected state and inspector tuning does not leave ghosting.
- [ ] Run: `dotnet build "Godot WarpGrid Test.sln"`
- [ ] Expected: build succeeds and the manager compiles with the new exported properties and dispatch loop.

### Task 3: Rewrite the compute shader as a PBD solver

**Files:**
- Modify: `shaders/WarpGrid.glsl`

- [ ] Implement prediction from Verlet-style velocity: `predicted = current + (current - prev) * GlobalDamping`.
- [ ] Inject effector displacement exactly once per substep using `force_scaler = 1.0 / SubSteps` and a uniform flag that is enabled only on the first solver iteration for that substep.
- [ ] Project anchor and 4-neighbor distance constraints during each dispatch using the latest read buffer, with anchor correction weighted strongly enough to dominate drift.
- [ ] Apply hard perimeter reset to anchor points whenever `BoundaryPinning` is enabled.
- [ ] Integrate by writing the final projected position and the previous current position back into the write buffer, and store both into `positions_tex`.
- [ ] Run: `dotnet build "Godot WarpGrid Test.sln"`
- [ ] Expected: build succeeds and Godot can still compile/load the shader resources at runtime.

### Task 4: Update the display shader for velocity glow

**Files:**
- Modify: `shaders/WarpGridDisplay.gdshader`

- [ ] Sample current and previous positions from the texture, displace vertices by `current_pos - anchor`, and derive per-vertex energy from `length(current_pos - prev_pos)`.
- [ ] Replace the old scalar-height glow logic with a non-linear arcade bloom curve based on squared energy.
- [ ] Keep the existing grid/texture display-mode behavior and bilinear sampling path for the decoupled high-resolution mesh.
- [ ] Run: `dotnet build "Godot WarpGrid Test.sln"`
- [ ] Expected: C# build still passes and the shader file remains syntactically valid for Godot.

### Task 5: Verify the refactor

**Files:**
- Verify: `scripts/WarpGridManager.cs`
- Verify: `shaders/WarpGrid.glsl`
- Verify: `shaders/WarpGridDisplay.gdshader`

- [ ] Run: `dotnet build "Godot WarpGrid Test.sln"`
- [ ] Expected: exit code 0.
- [ ] Run: `git diff -- scripts/WarpGridManager.cs shaders/WarpGrid.glsl shaders/WarpGridDisplay.gdshader`
- [ ] Expected: diff shows PBD-specific state, exported controls, multi-dispatch loop, and velocity-glow shader changes only.
- [ ] Re-read the implementation against the user requirements: low-res physics/high-res visual mesh, live exported tuning, multi-dispatch ping-pong, anchor-dominant projection, optional boundary pinning, normalized force injection, and velocity-based glow.
