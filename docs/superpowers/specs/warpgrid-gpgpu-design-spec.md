# WarpGrid GPGPU — Design Specification

**Status:** Phase 5.1 shipped. See §15 (Phase 4 changelog + Phase 5 / 5.1 addendum with cell-count transition), §16 (`std140` UBO 64 B reference), §17 (`RestState` SSBO softness-map reference). Plans: `../plans/2026-04-18-warpgrid-phase4.md`, `../plans/2026-04-18-warpgrid-gpgpu-prototype.md`.
**Target:** Godot 4.6.2-mono, Windows d3d12, Mobile renderer.
**Authors:** Shilo + Claude (Opus 4.7).
**Last updated:** 2026-04-18.

---

## 1. Goal

Reactive GPU-driven mass-spring grid. 10,000 nodes (100×100). Visual look: the taut elastic membrane of *Geometry Wars: Retro Evolved* — ripples propagate, reflect off anchored edges, carry momentum, decay over seconds rather than frames. Effectors (game entities: bullets, explosions, player) deform the grid via radial or line-segment forces.

**Success criteria:**
1. Moving effector produces smooth traveling ripples with visible wake.
2. Wavefront reflects cleanly off the anchored perimeter; interference patterns visible in the interior.
3. When effectors stop, oscillation decays over 1–3 seconds to a still grid.
4. After 60 s idle, the grid remains numerically pinned (no drift, no NaN).
5. Zero CPU readback. Zero per-frame vertex buffer rewrites.

## 2. Non-Goals

- GPU→CPU state readback (even for debug — use RenderDoc capture instead).
- Adaptive timestep / substepping.
- Physics integration with Godot's built-in physics servers.

(Runtime grid resize and per-cell anchor weighting were Phase 3 non-goals; both shipped in Phase 5 — see §15 addendum.)

## 3. Visual Target

Inspired by XNA "Neon Vector Shooter" tutorial. Key aesthetic properties:
- **Taut, not slack.** Rest length is `0.95 × initial_spacing` so every spring sits under slight tension. Without tension, the grid looks floppy.
- **Pull-only springs.** Springs only resist stretch, not compression. A compressed spring produces zero force. Result: you can dent the grid inward; it doesn't push back until you let go.
- **Persistent motion.** Light damping lets ripples echo. Heavy damping kills the life.
- **Crisp edges.** Perimeter nodes are frozen (position locked to rest, velocity zero). Creates clean reflections.

## 4. System Architecture

```
   ┌──────────────────────┐
   │ WarpEffector Node2D  │  (game entity: bullet, explosion, player)
   │ GlobalPosition       │
   │ Shape {Radial, Line} │
   │ Behavior {Force,Imp} │
   └──────────┬───────────┘
              │ ToData(origin, sizePx) → normalized [0,1] coords
              v
   ┌──────────────────────┐
   │ WarpGridManager      │  (driver, Node2D)
   │ _PhysicsProcess()    │  UploadEffectors → UploadParams → Dispatch → flip
   └──────────┬───────────┘
              │ BinaryWriter → byte[] → BufferUpdate
              v
   ┌──────────────────────────────────────────────────────────────┐
   │ RenderingDevice (MAIN, shared with canvas)                    │
   │                                                                │
   │   ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐  │
   │   │ StateA   │   │ StateB   │   │ Rest     │   │ Eff      │  │
   │   │ SSBO 16B │<->│ SSBO 16B │   │ SSBO 8B  │   │ SSBO 32B │  │
   │   └────┬─────┘   └────┬─────┘   └────┬─────┘   └────┬─────┘  │
   │        │ ping-pong    │              │              │         │
   │        v              v              v              v         │
   │   ┌──────────────────────────────────────────────────────┐   │
   │   │ WarpGrid.glsl   layout(local_size_x=y=8)             │   │
   │   │   Symplectic Euler + pull-only Hooke's + effectors   │   │
   │   │   imageStore(positions_tex, ivec2(c), new_pos)       │   │
   │   └──────────────────────┬───────────────────────────────┘   │
   │                          │                                    │
   │                          v                                    │
   │              ┌────────────────────────┐                       │
   │              │ positions_tex rg32f    │                       │
   │              │ image2D (W × H)        │                       │
   │              └────────────┬───────────┘                       │
   │                           │ Texture2DRD bridge                │
   └───────────────────────────┼────────────────────────────────────┘
                               │
                               v
   ┌──────────────────────────────────────────────────────────────┐
   │ Canvas render pass                                             │
   │                                                                │
   │   ┌──────────────────────────────────────────────┐            │
   │   │ WarpGridDisplay.gdshader (canvas_item)       │            │
   │   │   ivec2 c = ivec2(VERTEX_ID % W, VERTEX_ID/W)│            │
   │   │   VERTEX = texelFetch(tex, c, 0).rg * sizePx │            │
   │   └──────────────────┬───────────────────────────┘            │
   │                      │                                         │
   │                      v                                         │
   │         ┌─────────────────────┐                                │
   │         │ MeshInstance2D Lines│  19,800 line segments          │
   │         │ (static ArrayMesh)  │  = 39,600 indices              │
   │         └─────────────────────┘                                │
   └──────────────────────────────────────────────────────────────┘
```

**Key invariants:**
1. CPU never reads GPU buffers after init.
2. Static mesh topology; all displacement is GPU-side via `VERTEX_ID` texelFetch.
3. `ComputeListAddBarrier` ensures compute writes are visible before the canvas pass samples `positions_tex`.
4. MAIN RenderingDevice (`GetRenderingDevice()`) — NOT a local device — so compute and canvas share resources.
5. Godot's frame scheduler owns `Submit`/`Sync` on the main RD. Manager must not call these.

## 5. Data Contracts

All structs are `std430` on the GPU (SSBO) or `std140` (UBO); C# uses `BinaryWriter` (little-endian, matches every Godot-supported platform) over a pre-allocated `byte[]`.

### 5.1 `NodeState` — 16 bytes, std430
Dynamic per-node state, ping-ponged between `StateA` and `StateB`.
| Offset | Field | Type |
|-------:|-------|------|
| 0 | position | vec2 (normalized [0,1]) |
| 8 | velocity | vec2 |

### 5.2 `RestState` — 16 bytes, std430 (Phase 5)
Static per-node anchor + weight. Never modified after init (override `RestAnchorWeight(x, y)` + call `Rebuild()` to re-bake). See §17 for the softness-map pattern.
| Offset | Field | Type |
|-------:|-------|------|
| 0 | anchor | vec2 (normalized rest position) |
| 8 | weight | float (multiplies `rest_stiffness` + `rest_damping` per cell; default 1.0) |
| 12 | _pad | float (std430 stride rounds struct to 16 B) |

### 5.3 `WarpEffectorData` — 32 bytes, std430
| Offset | Field | Type |
|-------:|-------|------|
| 0 | start_point | vec2 (normalized) |
| 8 | end_point | vec2 (normalized; for Radial, `==start`; for Line, segment endpoint) |
| 16 | radius | float (normalized influence radius) |
| 20 | strength | float |
| 24 | shape_type | uint (0=Radial, 1=Line) |
| 28 | behavior_type | uint (0=Force continuous, 1=Impulse one-frame) |

C# mirror: `[StructLayout(LayoutKind.Sequential, Pack = 1)]` with identical field order. Size asserted via `Marshal.SizeOf<WarpEffectorData>() == 32` on `_Ready`.

### 5.4 `GridParams` UBO — 64 bytes, std140
| Offset | Field | Type | Notes |
|-------:|-------|------|-------|
| 0 | grid_size | uvec2 | `(W, H)` |
| 8 | grid_spacing | vec2 | `(1/(W-1), 1/(H-1))` normalized |
| 16 | dt | float | `1/60` |
| 20 | stiffness | float | 10.0 |
| 24 | damping | float | 0.45 |
| 28 | rest_stiffness | float | 6.0 |
| 32 | rest_damping | float | 0.10 |
| 36 | vel_damp | float | 0.92 |
| 40 | effector_count | uint | |
| 44 | rest_length_scale | float | 0.95 |
| 48 | impulse_cap | float | 0.5 |
| 52 | falloff_scale | float | 500.0 (Phase 5 — was `_pad0`; near-field sharpness in effector falloff denominator `FalloffScale·sp² + d²`) |
| 56 | grid_aspect | vec2 | `(pixel_w, pixel_h) / min(pixel_w, pixel_h)` — corrects anisotropic distance |

### 5.5 Shader bindings (descriptor set 0)
| Binding | Type | Role |
|--------:|------|------|
| 0 | SSBO readonly | `ReadState` (current frame) |
| 1 | SSBO writeonly | `WriteState` (next frame) |
| 2 | SSBO readonly | `RestBuf` |
| 3 | SSBO readonly | `EffBuf` |
| 4 | UBO | `GridParams` |
| 5 | image2D rg32f | `PositionsTex` (writeonly) |

## 6. Physics Model

### 6.1 Integration: Symplectic Euler
```
v_{t+1} = (v_t + F·dt + impulse_v) · vel_damp
if |v_{t+1}| < 1e-4: v_{t+1} = 0                // denormal prevention
p_{t+1} = p_t + v_{t+1}·dt                       // use NEW velocity (symplectic)
```

Symplectic Euler is chosen over explicit Euler for energy conservation in oscillatory systems — ripples persist naturally rather than drifting away.

### 6.2 Forces

**Neighbor springs (4-connected):** For each orthogonal neighbor, modified Hooke with explicit viscous damping along the spring axis:
```
delta = other - me
len   = |delta|
dir   = delta / len
x     = len - rest_len
if x < 0: force = 0                              // PULL-ONLY (tight membrane)
dv    = other_vel - me_vel
f     = stiffness·x - dot(dv, dir)·damping
force += dir · f
```

**Per-axis rest length:** Horizontal neighbors (`±x`) use `rest_len_x = grid_spacing.x * rest_length_scale`; vertical neighbors (`±y`) use `rest_len_y = grid_spacing.y * rest_length_scale`. On square grids they're equal; on rectangular grids each spring sits at its own natural rest length, avoiding the Phase 3 anisotropic-tension warning.

**Rest anchor (pull toward home):**
```
force += (rest - me_pos)·rest_stiffness - me_vel·rest_damping
```

**Effectors:** Variable-strength radial falloff. Center is `start_point` (Radial) or closest-point-on-segment (Line). Delta is aspect-corrected — `d = (node_pos - center) * grid_aspect` — so a pixel-defined radius (normalized by the grid's shorter pixel dimension) maps to a true circle on rectangular grids. Falloff denominators use `d2 = dot(d, d)`; force direction uses the raw (non-corrected) delta so push vectors stay geometrically correct.
- **Radial explosive** (`shape==0, start==end`): `2.5·S·d_raw / (10000·sp² + |d|²)`
- **Radial directed** (`shape==0, start≠end`): `1.0·S / (10·sp + |d|) · normalize(end-start)`
- **Line explosive** (`shape==1`): `2.5·S·d_raw / (10000·sp² + |d|²)` with center = closest point on segment.

Behavior routing:
- **Force** (`behavior==0`): accumulates into `force` (applied via `F·dt`).
- **Impulse** (`behavior==1`): converts to velocity directly, capped to `impulse_cap/dt` before adding to `new_vel`.

### 6.3 Boundary condition
Nodes where `c.x ∈ {0, W-1}` or `c.y ∈ {0, H-1}` are **frozen**: `position := rest`, `velocity := 0`, written to both SSBO and image. This creates clean reflections and prevents perimeter drift.

### 6.4 Stability analysis

For a 4-neighbor mass-spring lattice with mass=1, the CFL stability limit is:
```
k_max = 1 / (dt² · neighbors) = 1 / (1/60)² / 4 ≈ 3600
```

Current `stiffness = 10.0` sits ~2 orders of magnitude below the CFL limit — comfortable safety margin. The earlier value of `0.28` was a mis-calibration from pixel-space (where distances are ~1000) to normalized space (where distances are ~1.0), producing an "underwater" feel. Three damping sources (spring axial `0.45`, rest anchor `0.10`, global `vel_damp = 0.92`) are all dissipative; `0.92^60 ≈ 0.007` per second gives ~99% decay in 1 second (target: 0.5–1.0s ring before snap-to-zero).

**Impulse CFL cap:** One-frame impulse magnitude is clamped to `impulse_cap / dt = 30` (normalized units/s). Without this, a high-strength effector could inject one-frame velocity spikes that violate the CFL condition for the next step.

## 7. Rendering Pipeline

### 7.1 Mesh
Static `ArrayMesh` built once in `_Ready`:
- Vertices: `gridW × gridH` points in pixel space `[0, sizePx]`. Positions are "shape giver" only — the canvas shader overrides them.
- Indices: two-pass line connectivity. Horizontal pairs `(i, i+1)` skipping row-boundaries (`(i+1) % W == 0`). Vertical pairs `(i, i+W)` skipping end-of-grid (`i+W >= total`).
- Primitive: `Mesh.PrimitiveType.Lines`.
- For 100×100: 19,800 segments, 39,600 indices. No triangles.

### 7.2 Zero-readback displacement + velocity glow
`WarpGridDisplay.gdshader` (canvas_item):
````glsl
int w = grid_dims.x;
int id = int(VERTEX_ID);
ivec2 c = ivec2(id - (id / w) * w, id / w);
vec4  s = texelFetch(positions_tex, c, 0);
vec2  p = s.rg;
vec2  v = s.ba;
VERTEX = p * grid_size_pixels;

float speed = length(v);
float glow  = clamp(speed * glow_gain, 0.0, 1.0);
COLOR = mix(line_color, vec4(1.0), glow);
````
`positions_tex` is `rgba32f`; compute kernel writes `(pos, vel)` to `.rgba`, display shader reads both. `glow_gain` is a canvas-shader uniform (default 60).

### 7.3 Resource bridging: Texture2DRD
`Texture2DRD` is a `Resource` wrapper that holds a non-owning RID reference to an RD-created texture. Setting `TextureRdRid = _imgPositions` lets a standard `ShaderMaterial` parameter slot accept it as a `sampler2D`. The underlying image is still owned by the manager's RD; the manager must keep it alive for the lifetime of any material using it, and free it in `_ExitTree`.

## 8. Execution Flow

### 8.1 `_Ready`
```
Debug.Assert sizeof(WarpEffectorData) == 32
BuildMesh():      ArrayMesh + MeshInstance2D child
InitGpu():        shader, pipeline, 3 state SSBOs, effector SSBO, params UBO, storage image, 2 uniform sets (A→B, B→A)
BuildMaterial():  Texture2DRD wrapper, ShaderMaterial bound to positions_tex + grid_size_pixels + grid_dims + line_color
```
Guarded with `if (_rd != null) return;` for idempotency.
Guarded with `GridW/GridH >= 2` (hard error) and `GridW == GridH && sizeX ≈ sizeY` (warning) to catch anisotropic misuse.

### 8.2 `_PhysicsProcess` (60 Hz fixed)
```
UploadEffectors()     // enumerate Effectors[], pack 32 B each
UploadParams()        // write 52 meaningful bytes + 12 zeroed padding
Dispatch()            // compute list → bind → dispatch → barrier → end
_readIsA = !_readIsA  // flip ping-pong
```

### 8.3 `Dispatch`
```
list = rd.ComputeListBegin()
rd.ComputeListBindComputePipeline(list, _pipeline)
rd.ComputeListBindUniformSet(list, _readIsA ? _uniformSetA : _uniformSetB, 0)
rd.ComputeListDispatch(list, ceil(W/8), ceil(H/8), 1)
rd.ComputeListAddBarrier(list)       // compute-write → sampler-read hazard
rd.ComputeListEnd()
// DO NOT call Submit/Sync — main RD is engine-owned
```

### 8.4 `_ExitTree`
Uses `FreeIfValid(rid)` helper — each RID check via `rid.IsValid` so partial-init failures clean up without crashing. Frees all 9 owned RIDs. Does **not** call `_rd.Free()` (would free the engine's main RD).

## 9. Performance Characteristics

| Metric | Value |
|--------|-------|
| Grid | 100×100 = 10,000 nodes |
| Workgroup | 8×8 = 64 invocations |
| Dispatch | ⌈100/8⌉² = 169 workgroups |
| Compute memory | 10k × 16B (StateA) + 10k × 16B (StateB) + 10k × 8B (Rest) = 400 KB |
| Image | 10k × 8B rg32f = 80 KB |
| Effector buffer | 32 × 32B = 1 KB (capped to 32 effectors) |
| UBO | 64 B |
| Mesh buffer | 19,800 segments × 2 indices × 4B = 158 KB |
| Physics tick | 60 Hz fixed |

Zero per-frame GPU↔CPU transfer beyond the ~1–2 KB parameter/effector upload.

## 10. Files

| File | Responsibility |
|------|----------------|
| `scripts/WarpEffectorData.cs` | 32B GPU struct + `WarpShapeType`/`WarpBehaviorType` enums |
| `scripts/WarpEffector.cs` | Node2D entity; converts `GlobalPosition` → normalized grid space |
| `scripts/MeshHelper.cs` | Static grid vertex + line-index generation |
| `scripts/WarpGridManager.cs` | Driver: RD resource lifecycle, dispatch loop, ping-pong |
| `shaders/WarpGrid.glsl` | Compute kernel |
| `shaders/WarpGridDisplay.gdshader` | Canvas_item vertex shader — zero-readback bridge |
| `scenes/warpgrid_test.tscn` | Test scene: 100×100 grid, square 1000×1000 px, centered radial effector |

## 11. Known Limitations (deferred to Phase 4)

1. **Fixed `Dt = 1/60`** — ignores `Engine.PhysicsTicksPerSecond` changes. Phase 4 fix: pass `delta` into UBO each frame.
2. **No runtime grid resize** — uniform sets embed RIDs. Resize requires full teardown + re-init. Acceptable.
3. **No adaptive substepping** — strengths beyond plan values may overshoot CFL per frame. Phase 4 (if needed): 2-substep inner loop in compute shader.
4. **No interior anchor weighting** — uniform `rest_stiffness` across grid. XNA reference uses 3×3 loose anchor pattern. Phase 4 (if needed): add per-cell `k` to `RestState` (16 B/rest).
5. **Line count scaling** — 19,800 segments is fine. At 200×200 (79,400 segments) consider batching or instanced rendering.

## 12. Design Decisions Log

- **Main RD vs local RD.** Compute must share `positions_tex` with the canvas renderer. Local RD would require copy-on-readback or shared-memory tricks. Main RD trivially shares via `Texture2DRD`.
- **Ping-pong two buffers (not one with RMW).** Single-buffer race would force per-workgroup sync that GLSL compute cannot express without group-scoped barriers. Two-buffer ping-pong is textbook and trivially correct.
- **Pre-built uniform sets, not rebuilt each frame.** Two sets (A→B, B→A) are allocated once at init. Per-frame allocation would be wasteful and makes `_ExitTree` cleanup ambiguous.
- **`rgba32f` image (Phase 4).** Phase 3 used `rg32f` (pos-only). Phase 4 doubled to `rgba32f` so the compute kernel can also emit `(vel.x, vel.y)` alongside position, enabling velocity-proportional glow in the display shader without a second texture or a CPU-side readback.
- **`canvas_item` shader, not `spatial`.** WarpGrid is 2D; `canvas_item` exposes `VERTEX_ID` and gives the correct `VERTEX → 2D` pipeline without a camera projection matrix layer.
- **BinaryWriter over blittable C# structs.** Godot's `RenderingDevice.BufferUpdate` takes `byte[]`. `Marshal.StructureToPtr` would work but `BinaryWriter` is simpler and keeps the CPU-side layout visible in code.
- **`ComputeListAddBarrier` before `ComputeListEnd`.** The barrier API is confusing — it flushes the compute writes such that the _next_ operation in the list (or subsequent graphics pass) can read them. Placing it before `End` makes the canvas render pass's texture read properly sequenced.
- **Anisotropic fix (Phase 4).** Originally deferred via a square-grid test scene; Phase 4 introduced `grid_aspect` in the UBO + per-axis `rest_len_x`/`rest_len_y` in the spring loop, so rectangular grids no longer distort effector circles or spring tension.

## 13. Testing

Verification is visual + structural (per-task spec + code quality reviews already passed):
- ✅ Spec compliance reviews on each task (`WarpEffectorData`, `MeshHelper`, `WarpEffector`, `WarpGrid.glsl`, `WarpGridDisplay.gdshader`, `WarpGridManager`, test scene).
- ✅ Code quality reviews on each task with fix-review-fix loops.
- ✅ Final cross-cutting review: layout math verified end-to-end, zero-readback invariant structurally enforced, resource lifecycle tight, physics form correct.
- ⏳ **Runtime visual verification (user gate):** open editor → Build → Build Solution → F5. Expected: grid appears static; effector at (500,500) produces gentle radial dimple; moving the effector leaves a ripple wake; pulses reflect from edges.
- ⏳ **RenderDoc capture (on demand):** compute dispatch → image write → canvas sample chain should show no validation errors.

## 14. Future Direction

Phase 5 candidates (in priority order):
1. Dynamic `dt` injection (physics tick agnostic).
2. Per-cell rest-anchor weighting for non-uniform "stiffness fields."
3. Multiple grid instances for layered background effects.
4. Runtime grid resize (requires full re-init path).
5. Effector count > 128 via indirect dispatch or tiled dispatch.

## 15. Phase 4 Changelog

Shipped in Phase 4 (2026-04-18):
- **Binding 1 SSBO reflection fix** (`d2b74b6`) — dropped `restrict writeonly` qualifiers on bindings 0/1; Godot 4.6.2 SPIR-V reflection was misclassifying `writeonly buffer` as a texture type.
- **Effector auto-registration** via `warp_effectors` group; manager drops `Effectors` NodePath export.
- **AABB culling** — effectors outside the grid's world bounds skipped before GPU upload.
- **Per-axis anisotropy fix** — `grid_aspect` UBO field + aspect-corrected effector distance + per-axis spring rest_len.
- **MaxEffectors 32 → 128.**
- **Velocity channel** — `positions_tex` upgraded to `rgba32f`; compute writes `(pos, vel)`.
- **Velocity glow** in display shader — line brightness scales with local speed.
- **README tuning guide** at repo root.

### 15.1 Phase 5 / 5.1 Addendum (2026-04-18)

**Semantic break: `GridW` / `GridH` now count cells, not nodes.**

Phase 3 / 4 treated `GridW` as node count — `GridW=100` produced 100 vertices per row, 99 cells. Phase 5 flipped this: `GridW=100` produces 100 cells, 101 vertices per row. Motivation: matches user intuition ("I want a 10×10 grid" → 10 visible cells), and makes `grid_spacing = 1/GridW` exact rather than `1/(GridW-1)`.

Internally, `NodesX = GridW + 1` and `NodesY = GridH + 1` are private helpers. All buffer sizing, mesh generation, UBO `grid_size`, compute dispatch extents, and `positions_tex` dimensions are routed through `NodesX` / `NodesY`. The normalization factor in init (`nx = x / GridW`) intentionally uses the cell count, because `NodesX - 1 == GridW`.

Migration note: scenes from Phase 4 with `GridW = 100, GridH = 100` now render as a 100×100 **cell** grid (101×101 nodes) — one extra vertex per axis. Visually nearly identical; buffer sizes grow by ~2 %.

**Other Phase 5 / 5.1 shipped items:**

- **Fixed-step accumulator** — `_PhysicsProcess` accumulates `delta`, steps the compute in exact `Dt = 1/60` increments; catch-up capped at 8 steps to dodge spiral-of-death on frame hitches. Engine `PhysicsTicksPerSecond` changes no longer affect simulation feel.
- **Viscosity re-tune** — `RestDamping 0.4 → 1.2`, `VelDamp 0.88 → 0.85`, `FalloffScale 1000 → 500`, `glow_gain 20 → 8`. Snap-and-settle target (~0.5 s decay); residual motion near zero.
- **`FalloffScale` UBO field** — replaces Phase 4's `_pad0` at offset 52. Effector near-field sharpness; see §16 for layout.
- **`RestState.weight`** — per-cell anchor weight (std430 stride bumped 8 B → 16 B). Overridable via `protected virtual float RestAnchorWeight(int x, int y)` on a `WarpGridManager` subclass. See §17 for softness-map pattern.
- **Runtime resize** — `GridW`, `GridH`, `GridSizePixels` are now properties; setters call `Rebuild()` (teardown + re-init) via a `_rd != null` guard that keeps scene-load safe. `Rebuild()` is also public for explicit post-`RestAnchorWeight`-override re-bakes.
- **Effector visibility filter (5.1)** — `UploadEffectors` skips `!eff.Visible`, so hiding a `WarpEffector` zeroes its grid contribution without freeing anything. Basis of mouse-driven interaction controllers that toggle an effector on mouse press.

## 16. `GridParams` UBO — `std140` 64 B Reference

The compute shader reads a single `uniform GridParams` block. Layout is `std140` 64 B, and **both** the GLSL block and the C# `UploadParams` writer must agree to the byte — any drift silently corrupts downstream reads.

### `std140` alignment rules applied here

- `float` / `uint`: 4 B align, 4 B stride.
- `vec2` / `uvec2`: **8 B align**, 8 B stride.
- `vec4`: 16 B align, 16 B stride.
- Block total rounds up to 16 B.

The 64 B block is tightly packed — no natural padding beyond offset 52, which Phase 4 left as `_pad0` (for the next `vec2` at 56) and Phase 5 filled with `falloff_scale`. There is **no** trailing pad; the block happens to end exactly at 64 B.

| Offset | Field | GLSL | C# | Notes |
|-------:|-------|------|-----|-------|
| 0 | `grid_size` | `uvec2` | `bw.Write((uint)NodesX); bw.Write((uint)NodesY);` | Node count, not cell count — the compute kernel iterates over vertices. |
| 8 | `grid_spacing` | `vec2` | `bw.Write(1f/GridW); bw.Write(1f/GridH);` | Normalized spacing between adjacent nodes. `1/GridW` because `NodesX - 1 == GridW`. |
| 16 | `dt` | `float` | `bw.Write(Dt);` | 1/60 s fixed. |
| 20 | `stiffness` | `float` | | Neighbor-spring `k`. |
| 24 | `damping` | `float` | | Axial viscous damping along springs. |
| 28 | `rest_stiffness` | `float` | | Pull-to-rest `k`. Multiplied by per-cell `weight`. |
| 32 | `rest_damping` | `float` | | Per-node velocity drag near rest. Multiplied by per-cell `weight`. |
| 36 | `vel_damp` | `float` | | Global per-tick velocity multiplier. |
| 40 | `effector_count` | `uint` | | Number of active effectors (≤ `MaxEffectors = 128`). |
| 44 | `rest_length_scale` | `float` | | Spring rest length = `grid_spacing · rest_length_scale`. |
| 48 | `impulse_cap` | `float` | | CFL-safe cap on one-frame impulse injection. |
| 52 | `falloff_scale` | `float` | | Phase 5 — effector near-field sharpness in denominator `FalloffScale·sp² + d²`. |
| 56 | `grid_aspect` | `vec2` | `bw.Write(sx/min); bw.Write(sy/min);` | Anisotropy correction; unity on square grids. |

**When extending the UBO:**

1. Update both the GLSL block and the C# writer in the same commit.
2. Respect alignment: a new `vec2` needs an 8 B-aligned offset; a new `vec4` needs 16 B. Add explicit `float _padN` fields to round up.
3. Bump `ParamSize` in `WarpGridManager.cs` if total exceeds 64 B; grow `_paramScratch` accordingly.
4. Re-read this section before touching the writer — offset drift bugs are silent and look like tuning bugs.

## 17. `RestState` SSBO — Softness-Map Reference

`RestState` is a per-node static buffer written once at init and read every tick by the compute kernel. Phase 5 widened the struct from `{ vec2 anchor }` (8 B) to `{ vec2 anchor, float weight, float _pad }` (16 B, `std430` stride) so a scalar weight can scale `rest_stiffness` and `rest_damping` locally — without touching the UBO or recompiling the shader.

### GLSL

```glsl
struct RestState {
    vec2  anchor;
    float weight;
    float _pad;
};
layout(std430, binding = 2) readonly buffer RestBuf {
    RestState rest[];
};
// Usage (inside main()):
RestState r = rest[idx];
vec2 anchor_force = (r.anchor - me_pos) * rest_stiffness * r.weight
                  - me_vel               * rest_damping   * r.weight;
```

### C# init (excerpt)

```csharp
const int RestStride = 16; // std430 rounds {vec2,float,float} to 16 B
for (int y = 0; y < NodesY; y++)
    for (int x = 0; x < NodesX; x++)
    {
        float nx = (float)x / GridW;
        float ny = (float)y / GridH;
        rBw.Write(nx); rBw.Write(ny);
        rBw.Write(RestAnchorWeight(x, y)); // ← softness-map hook
        rBw.Write(0.0f);                    // std430 trailing pad
    }
```

### Softness-map authoring

Subclass `WarpGridManager` and override `RestAnchorWeight`:

```csharp
protected override float RestAnchorWeight(int x, int y)
{
    // Example: soft interior, stiff border. Edge nodes are already frozen,
    // but the 2-node ring inside them gets extra pull — cleaner reflections.
    int ring = Mathf.Min(Mathf.Min(x, y), Mathf.Min(NodesX - 1 - x, NodesY - 1 - y));
    if (ring <= 2)  return 2.0f;          // edge-adjacent → stiffer anchor
    if (ring >= 10) return 0.5f;          // deep interior → softer anchor
    return 1.0f;
}
```

Changes take effect on the next `Rebuild()` call. Typical flow:

```csharp
var mgr = GetNode<MyCustomWarpGrid>("WarpGridManager");
// … mutate map state …
mgr.Rebuild();  // teardown + re-init, including re-baking RestState
```

### Authoring guidelines

- `weight = 1.0` — baseline; matches Phase 3 / 4 behavior.
- `weight > 1.0` — stiffer anchor; node snaps home faster, ripples decay quicker in that region.
- `weight < 1.0` — softer anchor; ripples linger, node wanders further from rest.
- `weight = 0.0` — effectively unanchored (neighbor springs + global `vel_damp` still apply). The node will drift freely until pulled by a neighbor spring chain.
- Keep per-cell weight bounded (~0.1 – 5.0). The CFL analysis in §6.4 assumes `rest_stiffness ≤ ~3600`; locally multiplying by 5 still leaves a comfortable safety margin at `rest_stiffness = 6.0`.
- Softness maps are static — baking large maps (100×100) in `RestAnchorWeight` takes microseconds; no runtime cost beyond a single `Rebuild()`.
