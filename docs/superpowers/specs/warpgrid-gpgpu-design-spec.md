# WarpGrid GPGPU — Design Specification

**Status:** Phase 3 implementation shipped (see `../plans/2026-04-18-warpgrid-gpgpu-prototype.md`).
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
- Runtime grid resize.
- Physics integration with Godot's built-in physics servers.
- Interior anchor weighting variation (uniform rest-anchor stiffness only).

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

### 5.2 `RestState` — 8 bytes, std430
Static per-node anchor. Never modified after init.
| Offset | Field | Type |
|-------:|-------|------|
| 0 | anchor | vec2 |

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
| 20 | stiffness | float | 10.0 (calibrated for [0,1] space) |
| 24 | damping | float | 0.45 |
| 28 | rest_stiffness | float | 6.0 |
| 32 | rest_damping | float | 0.10 |
| 36 | vel_damp | float | 0.92 |
| 40 | effector_count | uint | |
| 44 | rest_length_scale | float | 0.95 |
| 48 | impulse_cap | float | 0.5 |
| 52 | _pad0 | float | 0 |
| 56–63 | — | — | block padding (std140 round up to 16-byte multiple of vec4 boundary) |

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

**Rest anchor (pull toward home):**
```
force += (rest - me_pos)·rest_stiffness - me_vel·rest_damping
```

**Effectors:** Variable-strength radial falloff. Center is `start_point` (Radial) or closest-point-on-segment (Line). Numerators are calibrated for normalized [0,1] space; denominators control falloff shape.
- **Radial explosive** (`shape==0, start==end`): `2.5·S·d / (10000·sp² + |d|²)`
- **Radial directed** (`shape==0, start≠end`): `1.0·S / (10·sp + |d|) · normalize(end-start)`
- **Line explosive** (`shape==1`): `2.5·S·d / (10000·sp² + |d|²)` with center = closest point on segment.

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

### 7.2 Zero-readback displacement
`WarpGridDisplay.gdshader` (canvas_item):
```glsl
int w = grid_dims.x;
int id = int(VERTEX_ID);
ivec2 c = ivec2(id - (id / w) * w, id / w);
vec2 p = texelFetch(positions_tex, c, 0).rg;
VERTEX = p * grid_size_pixels;
```
`VERTEX_ID` in `canvas_item` vertex shader is the linear index into the mesh's vertex buffer — matching the exact `y*W + x` order used by `MeshHelper.BuildGridVertices`. Vertex N reads texel `(N%W, N/W)` (expressed via subtraction to avoid the `%` operator) and gets the compute shader's latest position.

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

1. **Anisotropic on non-square grids** — `Radius` normalized by `gridSizePixels.X` only; `rest_len` uses `grid_spacing.x`. Current runtime warns via `PushWarning`. Phase 4 fix: per-axis normalization, or pick `min(x, y)` consistently across effector + spring paths.
2. **Fixed `Dt = 1/60`** — ignores `Engine.PhysicsTicksPerSecond` changes. Phase 4 fix: pass `delta` into UBO each frame.
3. **No runtime grid resize** — uniform sets embed RIDs. Resize requires full teardown + re-init. Acceptable.
4. **No adaptive substepping** — strengths beyond plan values may overshoot CFL per frame. Phase 4 (if needed): 2-substep inner loop in compute shader.
5. **No interior anchor weighting** — uniform `rest_stiffness` across grid. XNA reference uses 3×3 loose anchor pattern. Phase 4 (if needed): add per-cell `k` to `RestState` (16 B/rest).
6. **Line count scaling** — 19,800 segments is fine. At 200×200 (79,400 segments) consider batching or instanced rendering.

## 12. Design Decisions Log

- **Main RD vs local RD.** Compute must share `positions_tex` with the canvas renderer. Local RD would require copy-on-readback or shared-memory tricks. Main RD trivially shares via `Texture2DRD`.
- **Ping-pong two buffers (not one with RMW).** Single-buffer race would force per-workgroup sync that GLSL compute cannot express without group-scoped barriers. Two-buffer ping-pong is textbook and trivially correct.
- **Pre-built uniform sets, not rebuilt each frame.** Two sets (A→B, B→A) are allocated once at init. Per-frame allocation would be wasteful and makes `_ExitTree` cleanup ambiguous.
- **`rg32f` image, not `rgba32f`.** Only `x,y` are needed; halving bandwidth cost. `sampler2D` reads get `.rg` directly.
- **`canvas_item` shader, not `spatial`.** WarpGrid is 2D; `canvas_item` exposes `VERTEX_ID` and gives the correct `VERTEX → 2D` pipeline without a camera projection matrix layer.
- **BinaryWriter over blittable C# structs.** Godot's `RenderingDevice.BufferUpdate` takes `byte[]`. `Marshal.StructureToPtr` would work but `BinaryWriter` is simpler and keeps the CPU-side layout visible in code.
- **`ComputeListAddBarrier` before `ComputeListEnd`.** The barrier API is confusing — it flushes the compute writes such that the _next_ operation in the list (or subsequent graphics pass) can read them. Placing it before `End` makes the canvas render pass's texture read properly sequenced.
- **Square test grid.** Defers anisotropic-radius fix. Cheap now; can fix in Phase 4 without reworking the test.

## 13. Testing

Verification is visual + structural (per-task spec + code quality reviews already passed):
- ✅ Spec compliance reviews on each task (`WarpEffectorData`, `MeshHelper`, `WarpEffector`, `WarpGrid.glsl`, `WarpGridDisplay.gdshader`, `WarpGridManager`, test scene).
- ✅ Code quality reviews on each task with fix-review-fix loops.
- ✅ Final cross-cutting review: layout math verified end-to-end, zero-readback invariant structurally enforced, resource lifecycle tight, physics form correct.
- ⏳ **Runtime visual verification (user gate):** open editor → Build → Build Solution → F5. Expected: grid appears static; effector at (500,500) produces gentle radial dimple; moving the effector leaves a ripple wake; pulses reflect from edges.
- ⏳ **RenderDoc capture (on demand):** compute dispatch → image write → canvas sample chain should show no validation errors.

## 14. Future Direction

Phase 4 candidates (in priority order):
1. Per-axis normalization fix (unlocks rectangular grids for wide-aspect games).
2. Dynamic `dt` injection (physics tick agnostic).
3. Effector count > 32 via indirect dispatch or tiled dispatch.
4. Per-cell rest-anchor weighting for non-uniform "stiffness fields."
5. Multiple grid instances for layered background effects.
6. Runtime grid resize (requires full re-init path).
