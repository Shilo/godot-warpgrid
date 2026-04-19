# WarpGrid GPGPU Prototype Implementation Plan (v3 — strict zero-readback, MeshHelper)

> **Status (2026-04-18):** Phase 3 complete — Tasks 1–7 shipped. Task 8 (visual tuning) gated on user opening editor + running. See `docs/superpowers/specs/warpgrid-gpgpu-design-spec.md` for the design document.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## Phase 3 Completion Status

| Task | Status | Commit |
|------|--------|--------|
| 1. `WarpEffectorData.cs` | ✅ Done | `390a6d4` |
| 2. `WarpEffector.cs` | ✅ Done | `02cc13f` |
| 2b. `MeshHelper.cs` | ✅ Done | `17fd5f3` |
| 3. `WarpGrid.glsl` | ✅ Done | `c5c5049` |
| 4. `WarpGridDisplay.gdshader` | ✅ Done | `943844e` |
| 5. `WarpGridManager.cs` (init) | ✅ Done | `8f7daf8` |
| 6. `_PhysicsProcess` dispatch | ✅ Done | (merged into `8f7daf8`) |
| 7. Test scene | ✅ Done | `1d09192` |
| — Review fixes | ✅ Done | `7c09450`, `defde0a`, `d6f74a0` |
| 8. Visual tuning | ⏳ User gate | — |

### Post-Implementation Fixes (from code review)

- **`7c09450`** — `WarpGridManager` hardening: `_Ready` idempotency guard, `FreeIfValid` helper for partial-init safety, `LocalSize=8` constant linked to shader, std140/std430 layout comments on upload methods.
- **`defde0a`** — Test scene polish: stripped placeholder scene UID (editor regenerates), dropped no-op `EndOffset` on radial effector.
- **`d6f74a0`** — Non-square grid guard: `_Ready` throws if `GridW/H < 2`, `PushWarning` on non-square or unequal `GridSizePixels` (anisotropic radius/rest_len is deferred to Phase 4).

### Intentional Deviations from Spec

- Test scene uses **square 1000×1000 px grid** instead of plan's 1600×900. Reason: `WarpEffector.ToData` normalizes radius by `gridSizePixels.X` only and shader uses `grid_spacing.x` for rest_len — both produce anisotropy on non-square grids. Square grid sidesteps the issue until Phase 4 per-axis fix.
- `WarpGridManager.GridSizePixels` default changed from `(1600,900)` to `(1000,1000)` to match scene.

---

**Goal:** 100×100 GPU-driven reactive mass-spring grid. Compute kernel writes positions to an RD-backed texture; a `canvas_item` shader on a `MeshInstance2D` samples the texture via `VERTEX_ID`. Zero CPU readback. Supports Radial and Line-segment effectors with Force and Impulse behaviors. Look: tight, elastic "Geometry Wars: Retro Evolved" membrane.

**Architecture (revised):**
- **Main RenderingDevice** (`RenderingServer.GetRenderingDevice()`) — NOT a local device, so resources can be shared with the canvas renderer.
- **Three resources for physics state:**
  - `RestBuf` — static SSBO of `vec2 anchor` per node (8 bytes × N).
  - `StateA`, `StateB` — dynamic SSBOs of `{vec2 position, vec2 velocity}` per node (16 bytes × N). Ping-pong each frame.
- **`PositionsTex`** — R32G32_SFLOAT image, size `GRID_W × GRID_H`. Compute writes final position here via `imageStore`. Wrapped by a `Texture2DRD` and passed as `sampler2D` to the canvas shader.
- **Canvas shader on `MeshInstance2D`** uses `VERTEX_ID → texelFetch(positions, ...)` to displace each line vertex on the GPU — no CPU touches vertex data after init.
- **Explicit barrier** (`ComputeListAddBarrier`) between dispatch and end ensures `PositionsTex` is fully written before the frame's render pass reads it.
- **Symplectic Euler**: `v += F*dt; p += v_new*dt`. Audited — stable with current constants.

**Tech Stack:** Godot 4.6.2-mono, d3d12 (Mobile renderer supports RD), C#, GLSL compute + `canvas_item` shading language.

---

## File Structure

- `scripts/WarpEffectorData.cs` — 32-byte GPU struct (radial + line shapes, force + impulse behaviors)
- `scripts/WarpEffector.cs` — Node2D; reports config + endpoints to manager
- `scripts/MeshHelper.cs` — static: builds `Lines`-primitive index buffer for an N×M grid (horizontal `[i,i+1]` + vertical `[i,i+W]` segments)
- `scripts/WarpGridManager.cs` — driver; owns buffers, pipeline, textures, mesh, material
- `shaders/WarpGrid.glsl` — compute kernel
- `shaders/WarpGridDisplay.gdshader` — canvas_item shader that samples PositionsTex via `VERTEX_ID`
- `scenes/warpgrid_test.tscn` — test scene

### Why a separate MeshHelper?
The line-connectivity index pattern is the single piece of "wiring" that produces the Geometry Wars lattice look from a cloud of points. Isolating it makes the logic obvious, testable, and reusable if we add a second debug view (e.g., triangulated fill, or a second grid overlay).

---

## Physics Constants

| Constant | Value | Notes |
|---|---|---|
| `GRID_W × GRID_H` | 100×100 | 10,000 nodes |
| Workgroup | 8×8 | dispatch = ceil(100/8) = 13×13 = 169 groups |
| Main spring stiffness | `0.28` | |
| Main spring damping | `0.06` | |
| Rest-anchor stiffness | `0.10` | pull toward home |
| Rest-anchor damping | `0.10` | |
| Velocity damp/frame | `0.98` | |
| Velocity snap threshold | `1e-4` | in normalized space |
| Rest length scale | `0.95` | target = 0.95 × initial spacing — produces the taut-membrane tension |
| dt | `1/60` | fixed `_PhysicsProcess` |
| Impulse force cap | `F*dt ≤ 0.5` | per audit: prevent CFL overshoot from big effectors |

Effector force formulas (normalized grid space, `sp = grid_spacing.x`):
- **Radial-Explosive:** `100 * strength * (node - p) / (10000*sp² + d²)`
- **Radial-Implosive:** `10  * strength * (p - node)  / (100*sp² + d²)`
- **Radial-Directed:** `10  * strength / (10*sp + d) * direction` (direction = normalize(end - start))
- **Line-any:** compute closest point `p = start + clamp(dot(node-start, end-start)/dot(end-start,end-start), 0, 1) * (end-start)`, then apply the chosen radial formula with `p` as the center. This gives smooth bullet/laser wakes.

Boundaries (x==0, x==W-1, y==0, y==H-1): position locked to rest, velocity forced to 0 (anchored).

---

## Data Contract

### Dynamic state SSBO — `NodeState` (16 bytes, std430)
```glsl
struct NodeState {
    vec2 position;  // normalized [0,1]
    vec2 velocity;
};
```

### Static rest SSBO — `RestState` (8 bytes, std430)
```glsl
struct RestState {
    vec2 anchor;
};
```

### Effector SSBO — `WarpEffectorData` (32 bytes)
```glsl
struct WarpEffectorData {
    vec2  start_point;      // normalized; for radial, start==end
    vec2  end_point;        // normalized
    float radius;           // normalized influence radius
    float strength;         // scalar force multiplier
    uint  shape_type;       // 0 = Radial, 1 = Line
    uint  behavior_type;    // 0 = Force (continuous), 1 = Impulse (one-frame velocity kick)
};
```

### UBO `GridParams` (std140, 64 bytes after padding)
```glsl
layout(binding = 4, std140) uniform GridParams {
    uvec2 grid_size;         // 8
    vec2  grid_spacing;      // 8 — normalized distance between nodes (1/(W-1), 1/(H-1))
    float dt;                // 4
    float stiffness;         // 4
    float damping;           // 4
    float rest_stiffness;    // 4
    float rest_damping;      // 4
    float vel_damp;          // 4
    uint  effector_count;    // 4
    float rest_length_scale; // 4  (0.95)
    float impulse_cap;       // 4  (0.5)
    float _pad0;             // 4
};
```

### C# mirror `WarpEffectorData` (32 bytes, `[StructLayout(Sequential, Pack=1)]`)
Byte-for-byte match with the GLSL struct.

### Shader bindings (set 0)
- 0: `ReadState`   — SSBO, read  (State ping or pong)
- 1: `WriteState`  — SSBO, write (the other)
- 2: `RestBuf`     — SSBO, read
- 3: `EffBuf`      — SSBO, read (effectors)
- 4: `GridParams`  — UBO
- 5: `PositionsTex` — `image2D rg32f` — write-only

---

## Task 1: Create `WarpEffectorData.cs`

**Files:**
- Create: `scripts/WarpEffectorData.cs`

- [x] **Step 1: Write the struct**

```csharp
using System.Runtime.InteropServices;
using Godot;

namespace WarpGrid;

public enum WarpShapeType    : uint { Radial = 0, Line = 1 }
public enum WarpBehaviorType : uint { Force  = 0, Impulse = 1 }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WarpEffectorData
{
    public Vector2 StartPoint;     // 8
    public Vector2 EndPoint;       // 8
    public float   Radius;          // 4
    public float   Strength;        // 4
    public uint    ShapeType;       // 4
    public uint    BehaviorType;    // 4
}
// sizeof = 32 bytes
```

- [x] **Step 2: Commit**

```bash
git add scripts/WarpEffectorData.cs
git commit -m "feat: add WarpEffectorData 32-byte GPU struct (radial+line, force+impulse)"
```

---

## Task 2: Create `WarpEffector.cs`

**Files:**
- Create: `scripts/WarpEffector.cs`

- [x] **Step 1: Write the node**

```csharp
using Godot;

namespace WarpGrid;

[GlobalClass]
public partial class WarpEffector : Node2D
{
    [Export] public WarpShapeType    Shape    = WarpShapeType.Radial;
    [Export] public WarpBehaviorType Behavior = WarpBehaviorType.Force;
    [Export] public float   Radius   = 200.0f;   // pixels
    [Export] public float   Strength = 1.0f;
    // For Line shape: EndOffset is in pixels, relative to GlobalPosition.
    // For Radial: EndOffset is ignored (StartPoint == EndPoint == GlobalPosition).
    [Export] public Vector2 EndOffset = Vector2.Right * 100.0f;
    // For Radial-Directed, you can pack direction in EndOffset as the "end" point;
    // the shader picks up direction from (end - start) when shape == Radial and |end-start| > 0.

    public WarpEffectorData ToData(Vector2 gridOrigin, Vector2 gridSizePixels)
    {
        var startNorm = (GlobalPosition - gridOrigin) / gridSizePixels;
        var endNorm   = Shape == WarpShapeType.Line
            ? (GlobalPosition + EndOffset - gridOrigin) / gridSizePixels
            : startNorm;
        return new WarpEffectorData
        {
            StartPoint   = startNorm,
            EndPoint     = endNorm,
            Radius       = Radius / gridSizePixels.X,
            Strength     = Strength,
            ShapeType    = (uint)Shape,
            BehaviorType = (uint)Behavior,
        };
    }
}
```

- [x] **Step 2: Commit**

```bash
git add scripts/WarpEffector.cs
git commit -m "feat: add WarpEffector Node2D (radial+line, force+impulse)"
```

---

## Task 2b: Create `MeshHelper.cs`

**Files:**
- Create: `scripts/MeshHelper.cs`

**Line-connectivity pattern (10,000 nodes, 100×100):**
- Horizontal segments: for every row `y`, for every `x` in `[0, W-2]`, emit `(y*W + x, y*W + x + 1)`.
- Vertical segments: for every column `x`, for every `y` in `[0, H-2]`, emit `(y*W + x, (y+1)*W + x)`.
- Total segments: `H*(W-1) + W*(H-1)` = 100·99 + 100·99 = 19,800 lines = 39,600 indices.

- [x] **Step 1: Write the helper**

```csharp
using Godot;

namespace WarpGrid;

public static class MeshHelper
{
    /// Generates a flat grid of vertices in normalized [0,1] space, then scaled by sizePixels.
    public static Vector2[] BuildGridVertices(int gridW, int gridH, Vector2 sizePixels)
    {
        var verts = new Vector2[gridW * gridH];
        for (int y = 0; y < gridH; y++)
            for (int x = 0; x < gridW; x++)
                verts[y * gridW + x] = new Vector2(
                    (float)x / (gridW - 1) * sizePixels.X,
                    (float)y / (gridH - 1) * sizePixels.Y);
        return verts;
    }

    /// Builds the index buffer for a line-grid: horizontal + vertical segments.
    /// Intended for Mesh.PrimitiveType.Lines.
    public static int[] BuildLineGridIndices(int gridW, int gridH)
    {
        int hLines = gridH * (gridW - 1);   // 100 * 99 = 9,900
        int vLines = gridW * (gridH - 1);   // 100 * 99 = 9,900
        var indices = new int[(hLines + vLines) * 2];
        int k = 0;

        // Horizontal segments: node[i] -> node[i+1]
        for (int y = 0; y < gridH; y++)
            for (int x = 0; x < gridW - 1; x++)
            {
                int i = y * gridW + x;
                indices[k++] = i;
                indices[k++] = i + 1;
            }

        // Vertical segments: node[i] -> node[i + gridW]
        for (int y = 0; y < gridH - 1; y++)
            for (int x = 0; x < gridW; x++)
            {
                int i = y * gridW + x;
                indices[k++] = i;
                indices[k++] = i + gridW;
            }

        return indices;
    }
}
```

- [x] **Step 2: Commit**

```bash
git add scripts/MeshHelper.cs
git commit -m "feat: MeshHelper — line-grid vertex + index generation"
```

---

## Task 3: Write `WarpGrid.glsl` compute shader

**Files:**
- Create: `shaders/WarpGrid.glsl`

- [x] **Step 1: Write the shader**

```glsl
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

struct NodeState       { vec2 position; vec2 velocity; };
struct RestState       { vec2 anchor; };
struct WarpEffectorData {
    vec2  start_point;
    vec2  end_point;
    float radius;
    float strength;
    uint  shape_type;
    uint  behavior_type;
};

layout(set = 0, binding = 0, std430) restrict readonly  buffer ReadSt   { NodeState data[]; } r_in;
layout(set = 0, binding = 1, std430) restrict writeonly buffer WriteSt  { NodeState data[]; } r_out;
layout(set = 0, binding = 2, std430) restrict readonly  buffer RestBuf  { RestState data[]; } r_rest;
layout(set = 0, binding = 3, std430) restrict readonly  buffer EffBuf   { WarpEffectorData data[]; } r_eff;

layout(set = 0, binding = 4, std140) uniform GridParams {
    uvec2 grid_size;
    vec2  grid_spacing;
    float dt;
    float stiffness;
    float damping;
    float rest_stiffness;
    float rest_damping;
    float vel_damp;
    uint  effector_count;
    float rest_length_scale;
    float impulse_cap;
    float _pad0;
} p;

layout(set = 0, binding = 5, rg32f) uniform restrict writeonly image2D positions_tex;

uint idx(uvec2 c) { return c.y * p.grid_size.x + c.x; }

// Modified Hooke (stretched-only) with explicit viscous damping along the spring axis.
vec2 spring_force(vec2 me_pos, vec2 me_vel, vec2 other_pos, vec2 other_vel,
                  float rest_len, float k, float c) {
    vec2  delta = other_pos - me_pos;
    float len   = length(delta);
    if (len < 1e-7) return vec2(0.0);
    vec2  dir   = delta / len;
    float x     = len - rest_len;
    if (x < 0.0) return vec2(0.0); // tight look — no pushback on compression
    vec2  dv    = other_vel - me_vel;
    float f     = k * x - dot(dv, dir) * c;
    return dir * f;
}

// Returns the closest point on segment ab to p. Used for line-shape effectors.
vec2 closest_on_segment(vec2 a, vec2 b, vec2 p0) {
    vec2  ba = b - a;
    float denom = dot(ba, ba);
    if (denom < 1e-10) return a;
    float t = clamp(dot(p0 - a, ba) / denom, 0.0, 1.0);
    return a + t * ba;
}

vec2 effector_force(vec2 node_pos, WarpEffectorData e) {
    vec2 center = (e.shape_type == 1u)
        ? closest_on_segment(e.start_point, e.end_point, node_pos)
        : e.start_point;

    vec2  d  = node_pos - center;
    float d2 = dot(d, d);
    if (d2 > e.radius * e.radius) return vec2(0.0);

    // Radial "directed" mode: if start != end on a Radial shape, use the direction vector.
    if (e.shape_type == 0u) {
        vec2 dir_vec = e.end_point - e.start_point;
        if (dot(dir_vec, dir_vec) > 1e-10) {
            float dist = sqrt(d2);
            return 10.0 * e.strength / (10.0 * p.grid_spacing.x + dist) * normalize(dir_vec);
        }
        // Pure radial-explosive
        float denom = 10000.0 * p.grid_spacing.x * p.grid_spacing.x + d2;
        return 100.0 * e.strength * d / denom;
    }
    // Line shape: explosive push outward from the segment.
    float denom = 10000.0 * p.grid_spacing.x * p.grid_spacing.x + d2;
    return 100.0 * e.strength * d / denom;
}

void main() {
    uvec2 c = gl_GlobalInvocationID.xy;
    if (c.x >= p.grid_size.x || c.y >= p.grid_size.y) return;

    uint i = idx(c);
    NodeState me   = r_in.data[i];
    vec2      rest = r_rest.data[i].anchor;

    // Boundary freeze.
    if (c.x == 0u || c.y == 0u || c.x == p.grid_size.x - 1u || c.y == p.grid_size.y - 1u) {
        NodeState anchor;
        anchor.position = rest;
        anchor.velocity = vec2(0.0);
        r_out.data[i] = anchor;
        imageStore(positions_tex, ivec2(c), vec4(rest, 0.0, 0.0));
        return;
    }

    vec2 force    = vec2(0.0);
    float rest_len = p.grid_spacing.x * p.rest_length_scale;

    // 4-neighbor springs
    if (c.x > 0u) {
        NodeState n = r_in.data[idx(uvec2(c.x - 1u, c.y))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len, p.stiffness, p.damping);
    }
    if (c.x + 1u < p.grid_size.x) {
        NodeState n = r_in.data[idx(uvec2(c.x + 1u, c.y))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len, p.stiffness, p.damping);
    }
    if (c.y > 0u) {
        NodeState n = r_in.data[idx(uvec2(c.x, c.y - 1u))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len, p.stiffness, p.damping);
    }
    if (c.y + 1u < p.grid_size.y) {
        NodeState n = r_in.data[idx(uvec2(c.x, c.y + 1u))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len, p.stiffness, p.damping);
    }

    // Rest anchor pull.
    force += (rest - me.position) * p.rest_stiffness - me.velocity * p.rest_damping;

    // Continuous-force effectors.
    vec2 impulse_v = vec2(0.0);
    for (uint e = 0u; e < p.effector_count; e++) {
        WarpEffectorData ed = r_eff.data[e];
        vec2 ef = effector_force(me.position, ed);
        if (ed.behavior_type == 1u) {
            // Impulse: integrate directly into velocity, capped to prevent CFL overshoot.
            float mag = length(ef);
            if (mag > p.impulse_cap / max(p.dt, 1e-6)) {
                ef *= (p.impulse_cap / max(p.dt, 1e-6)) / max(mag, 1e-7);
            }
            impulse_v += ef * p.dt;
        } else {
            force += ef;
        }
    }

    // Symplectic Euler with impulse injection.
    vec2 new_vel = me.velocity + force * p.dt + impulse_v;
    new_vel *= p.vel_damp;
    if (length(new_vel) < 1e-4) new_vel = vec2(0.0);
    vec2 new_pos = me.position + new_vel * p.dt;

    NodeState result;
    result.position = new_pos;
    result.velocity = new_vel;
    r_out.data[i]   = result;

    imageStore(positions_tex, ivec2(c), vec4(new_pos, 0.0, 0.0));
}
```

- [x] **Step 2: Commit**

```bash
git add shaders/WarpGrid.glsl
git commit -m "feat: WarpGrid compute shader (radial+line, force+impulse, image output)"
```

---

## Task 4: Write `WarpGridDisplay.gdshader` (canvas_item)

**Files:**
- Create: `shaders/WarpGridDisplay.gdshader`

- [x] **Step 1: Write the shader**

```gdshader
shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2D positions_tex : filter_nearest, repeat_disable;
uniform vec2  grid_size_pixels = vec2(1600.0, 900.0);
uniform ivec2 grid_dims        = ivec2(100, 100);
uniform vec4  line_color : source_color = vec4(0.15, 0.55, 1.0, 1.0);

void vertex() {
    int w = grid_dims.x;
    ivec2 c = ivec2(VERTEX_ID % w, VERTEX_ID / w);
    vec2 p = texelFetch(positions_tex, c, 0).rg; // normalized [0,1]
    VERTEX = p * grid_size_pixels;
}

void fragment() {
    COLOR = line_color;
}
```

- [x] **Step 2: Commit**

```bash
git add shaders/WarpGridDisplay.gdshader
git commit -m "feat: canvas_item shader samples PositionsTex via VERTEX_ID"
```

---

## Task 5: Scaffold `WarpGridManager.cs` (init)

**Files:**
- Create: `scripts/WarpGridManager.cs`

- [x] **Step 1: Class skeleton + constants**

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace WarpGrid;

[GlobalClass]
public partial class WarpGridManager : Node2D
{
    [Export] public int GridW = 100;
    [Export] public int GridH = 100;
    [Export] public Vector2 GridSizePixels = new(1600, 900);
    [Export] public Godot.Collections.Array<NodePath> Effectors = new();
    [Export] public Color LineColor = new(0.15f, 0.55f, 1.0f);

    const float Stiffness     = 0.28f;
    const float Damping       = 0.06f;
    const float RestStiffness = 0.10f;
    const float RestDamping   = 0.10f;
    const float VelDamp       = 0.98f;
    const float Dt            = 1.0f / 60.0f;
    const float RestLenScale  = 0.95f;
    const float ImpulseCap    = 0.5f;
    const int   MaxEffectors  = 32;

    const int StateStride = 16;  // vec2 pos + vec2 vel
    const int RestStride  = 8;   // vec2 anchor
    const int EffStride   = 32;
    const int ParamSize   = 64;  // padded std140 — see shader

    RenderingDevice _rd;
    Rid _shader, _pipeline;
    Rid _bufStateA, _bufStateB, _bufRest, _bufEff, _bufParams;
    Rid _imgPositions;
    Rid _uniformSetA, _uniformSetB; // A = A→B, B = B→A
    bool _readIsA = true;

    ArrayMesh _mesh;
    MeshInstance2D _meshInstance;
    ShaderMaterial _material;
    Texture2Drd _positionsTexture;
    int _surfaceIndex = 0;

    byte[] _stateScratch, _restScratch, _effScratch, _paramScratch;
    uint   _effCount;
}
```

- [x] **Step 2: `_Ready()` — build mesh + material + GPU**

```csharp
public override void _Ready()
{
    System.Diagnostics.Debug.Assert(Marshal.SizeOf<WarpEffectorData>() == 32);
    BuildMesh();
    InitGpu();
    BuildMaterial();
}

void BuildMesh()
{
    _mesh = new ArrayMesh();

    // Mesh is a static scaffold — the MeshInstance2D never moves and vertex positions are
    // never rewritten from the CPU. The canvas shader displaces each vertex on the GPU by
    // sampling PositionsTex at ivec2(VERTEX_ID % W, VERTEX_ID / W). The initial positions
    // here just give the vertex buffer shape so VERTEX_ID is valid.
    var verts   = MeshHelper.BuildGridVertices(GridW, GridH, GridSizePixels);
    var indices = MeshHelper.BuildLineGridIndices(GridW, GridH);

    var arrays = new Godot.Collections.Array();
    arrays.Resize((int)Mesh.ArrayType.Max);
    arrays[(int)Mesh.ArrayType.Vertex] = verts;
    arrays[(int)Mesh.ArrayType.Index]  = indices;
    _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);

    _meshInstance = new MeshInstance2D { Mesh = _mesh };
    AddChild(_meshInstance);
}

void InitGpu()
{
    _rd = RenderingServer.GetRenderingDevice();
    if (_rd == null)
        throw new Exception("Main RenderingDevice unavailable — use Forward+ or Mobile renderer.");

    var shaderFile = GD.Load<RDShaderFile>("res://shaders/WarpGrid.glsl");
    var spirv = shaderFile.GetSpirV();
    _shader = _rd.ShaderCreateFromSpirV(spirv);
    _pipeline = _rd.ComputePipelineCreate(_shader);

    int n = GridW * GridH;

    // Build initial state (position = rest, velocity = 0).
    _stateScratch = new byte[n * StateStride];
    _restScratch  = new byte[n * RestStride];
    using (var sMs = new MemoryStream(_stateScratch))
    using (var sBw = new BinaryWriter(sMs))
    using (var rMs = new MemoryStream(_restScratch))
    using (var rBw = new BinaryWriter(rMs))
    {
        for (int y = 0; y < GridH; y++)
            for (int x = 0; x < GridW; x++)
            {
                float nx = (float)x / (GridW - 1);
                float ny = (float)y / (GridH - 1);
                sBw.Write(nx); sBw.Write(ny);     // position
                sBw.Write(0.0f); sBw.Write(0.0f); // velocity
                rBw.Write(nx); rBw.Write(ny);     // rest anchor
            }
    }

    _bufStateA = _rd.StorageBufferCreate((uint)_stateScratch.Length, _stateScratch);
    _bufStateB = _rd.StorageBufferCreate((uint)_stateScratch.Length, _stateScratch);
    _bufRest   = _rd.StorageBufferCreate((uint)_restScratch.Length,  _restScratch);

    _effScratch   = new byte[MaxEffectors * EffStride];
    _bufEff       = _rd.StorageBufferCreate((uint)_effScratch.Length, _effScratch);

    _paramScratch = new byte[ParamSize];
    _bufParams    = _rd.UniformBufferCreate(ParamSize, _paramScratch);

    // Positions output image — rg32f, size = grid dims.
    var fmt = new RDTextureFormat
    {
        Width             = (uint)GridW,
        Height            = (uint)GridH,
        Format            = RenderingDevice.DataFormat.R32G32Sfloat,
        UsageBits         = RenderingDevice.TextureUsageBits.StorageBit
                          | RenderingDevice.TextureUsageBits.SamplingBit
                          | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        TextureType       = RenderingDevice.TextureType.Type2D,
    };
    var view = new RDTextureView();
    _imgPositions = _rd.TextureCreate(fmt, view);

    _uniformSetA = CreateUniformSet(_bufStateA, _bufStateB);
    _uniformSetB = CreateUniformSet(_bufStateB, _bufStateA);
}

Rid CreateUniformSet(Rid readBuf, Rid writeBuf)
{
    RDUniform U(RenderingDevice.UniformType t, int bind, Rid id) {
        var u = new RDUniform { UniformType = t, Binding = bind };
        u.AddId(id);
        return u;
    }
    var set = new Godot.Collections.Array<RDUniform>
    {
        U(RenderingDevice.UniformType.StorageBuffer, 0, readBuf),
        U(RenderingDevice.UniformType.StorageBuffer, 1, writeBuf),
        U(RenderingDevice.UniformType.StorageBuffer, 2, _bufRest),
        U(RenderingDevice.UniformType.StorageBuffer, 3, _bufEff),
        U(RenderingDevice.UniformType.UniformBuffer, 4, _bufParams),
        U(RenderingDevice.UniformType.Image,         5, _imgPositions),
    };
    return _rd.UniformSetCreate(set, _shader, 0);
}

void BuildMaterial()
{
    _positionsTexture = new Texture2Drd { TextureRdRid = _imgPositions };

    var shader = GD.Load<Shader>("res://shaders/WarpGridDisplay.gdshader");
    _material = new ShaderMaterial { Shader = shader };
    _material.SetShaderParameter("positions_tex",     _positionsTexture);
    _material.SetShaderParameter("grid_size_pixels",  GridSizePixels);
    _material.SetShaderParameter("grid_dims",         new Vector2I(GridW, GridH));
    _material.SetShaderParameter("line_color",        LineColor);
    _meshInstance.Material = _material;
}
```

- [x] **Step 3: `_ExitTree()` cleanup**

```csharp
public override void _ExitTree()
{
    if (_rd == null) return;
    _rd.FreeRid(_uniformSetA); _rd.FreeRid(_uniformSetB);
    _rd.FreeRid(_imgPositions);
    _rd.FreeRid(_bufStateA); _rd.FreeRid(_bufStateB);
    _rd.FreeRid(_bufRest);   _rd.FreeRid(_bufEff);
    _rd.FreeRid(_bufParams);
    _rd.FreeRid(_pipeline);  _rd.FreeRid(_shader);
    // Do NOT call _rd.Free() — it's the main rendering device, owned by the engine.
    _rd = null;
}
```

- [x] **Step 4: Verify via Godot MCP**

Run `mcp__godot__run_project` headlessly, then `mcp__godot__get_debug_output`. Expected: no errors during `_Ready()`. If `shader_create_from_spirv` fails, verify the GLSL path and that spirv compilation works.

- [x] **Step 5: Commit**

```bash
git add scripts/WarpGridManager.cs
git commit -m "feat: WarpGridManager scaffold — main RD, SSBOs, image, mesh+material"
```

---

## Task 6: `_PhysicsProcess` — dispatch + ping-pong (no readback)

**Files:**
- Modify: `scripts/WarpGridManager.cs`

- [x] **Step 1: Add `_PhysicsProcess`**

```csharp
public override void _PhysicsProcess(double delta)
{
    UploadEffectors();
    UploadParams();
    Dispatch();
    _readIsA = !_readIsA;
}
```

- [x] **Step 2: `UploadEffectors`**

```csharp
void UploadEffectors()
{
    Array.Clear(_effScratch, 0, _effScratch.Length);
    int count = 0;
    var origin = GlobalPosition;

    using var ms = new MemoryStream(_effScratch);
    using var bw = new BinaryWriter(ms);
    foreach (var path in Effectors)
    {
        if (count >= MaxEffectors) break;
        var node = GetNodeOrNull<WarpEffector>(path);
        if (node == null) continue;
        var d = node.ToData(origin, GridSizePixels);
        bw.Write(d.StartPoint.X);  bw.Write(d.StartPoint.Y);
        bw.Write(d.EndPoint.X);    bw.Write(d.EndPoint.Y);
        bw.Write(d.Radius);
        bw.Write(d.Strength);
        bw.Write(d.ShapeType);
        bw.Write(d.BehaviorType);
        count++;
    }
    _effCount = (uint)count;
    _rd.BufferUpdate(_bufEff, 0, (uint)_effScratch.Length, _effScratch);
}
```

- [x] **Step 3: `UploadParams`**

```csharp
void UploadParams()
{
    Array.Clear(_paramScratch, 0, _paramScratch.Length);
    using var ms = new MemoryStream(_paramScratch);
    using var bw = new BinaryWriter(ms);
    bw.Write((uint)GridW); bw.Write((uint)GridH);            // uvec2 grid_size  (8)
    float sx = 1.0f / (GridW - 1);
    float sy = 1.0f / (GridH - 1);
    bw.Write(sx); bw.Write(sy);                                // vec2  grid_spacing (16)
    bw.Write(Dt);                                              // (20)
    bw.Write(Stiffness);                                       // (24)
    bw.Write(Damping);                                         // (28)
    bw.Write(RestStiffness);                                   // (32)
    bw.Write(RestDamping);                                     // (36)
    bw.Write(VelDamp);                                         // (40)
    bw.Write(_effCount);                                       // (44)
    bw.Write(RestLenScale);                                    // (48)
    bw.Write(ImpulseCap);                                      // (52)
    // bytes 52..64 left zero as _pad0 + std140 block padding
    _rd.BufferUpdate(_bufParams, 0, (uint)_paramScratch.Length, _paramScratch);
}
```

- [x] **Step 4: `Dispatch` (with explicit barriers)**

```csharp
void Dispatch()
{
    var set = _readIsA ? _uniformSetA : _uniformSetB;
    uint gx = (uint)((GridW + 7) / 8);
    uint gy = (uint)((GridH + 7) / 8);

    long list = _rd.ComputeListBegin();
    _rd.ComputeListBindComputePipeline(list, _pipeline);
    _rd.ComputeListBindUniformSet(list, set, 0);
    _rd.ComputeListDispatch(list, gx, gy, 1);
    // Ensure compute writes visible to the graphics pass that reads PositionsTex.
    _rd.ComputeListAddBarrier(list);
    _rd.ComputeListEnd();
    // On the MAIN RD we do NOT call Submit/Sync — Godot's frame scheduler handles it.
}
```

- [x] **Step 5: Verify**

Run project; call `mcp__godot__get_debug_output`. Expect: no validation errors from d3d12. Grid should already appear to drift/settle if anything is wrong; correct behavior with no effectors = perfectly still 100×100 grid.

- [x] **Step 6: Commit**

```bash
git add scripts/WarpGridManager.cs
git commit -m "feat: WarpGridManager dispatch + barriers, no CPU readback"
```

---

## Task 7: Test scene

**Files:**
- Create: `scenes/warpgrid_test.tscn`
- Modify: `project.godot` (set main scene)

- [x] **Step 1: Build scene via Godot MCP**

Use `mcp__godot__create_scene`:
- Root: `Node2D` `WarpGridRoot`
- Child: `WarpGridManager` (script attached), position `(160, 90)`, `GridSizePixels=(1600,900)`, `GridW=100`, `GridH=100`
- Child of manager: `WarpEffector` `Effector1`, position `(800,450)`, `Shape=Radial`, `Behavior=Force`, `Radius=300`, `Strength=5.0`
- Child of root: `Camera2D`, position `(960,540)`
- Set Effector1's path inside `WarpGridManager.Effectors`

- [x] **Step 2: Set main scene**

Update `project.godot`:
```
[application]
run/main_scene="res://scenes/warpgrid_test.tscn"
```

- [x] **Step 3: Verify scene wiring via MCP**

Use `mcp__godot__get_project_info` and any scene-dump tool available to confirm `MeshInstance2D` is under `WarpGridManager`, has `Material = WarpGridDisplay.gdshader`, and the `positions_tex` uniform is bound.

- [x] **Step 4: Commit**

```bash
git add scenes/warpgrid_test.tscn project.godot
git commit -m "feat: warpgrid test scene + main scene wiring"
```

---

## Task 8: Visual verification & tuning

**Files:**
- Modify: `scripts/WarpGridManager.cs` (constants) if targets not met

- [ ] **Moving effector:** add a quick test script that oscillates `Effector1.GlobalPosition` along a sine path. Watch the wake.
- [ ] **Ripple propagation:** wavefront reaches edges within ~1 s; no visible edge deformation (edges are frozen).
- [ ] **Edge ring/bounce:** after a pulse, reflections interfere visibly in the interior.
- [ ] **Memory:** when effector stops, oscillation decays over 1–3 s.
- [ ] **No NaN drift:** after 60 s idle, grid remains pinned. Inspect visually only — the runtime pipeline is pure GPU. (If deeper debugging ever needed, use `RenderDoc` frame capture, not CPU readback.)
- [ ] **Taut feel:** springs under slight tension thanks to rest-length 0.95. If it sags, raise `Stiffness` or `RestStiffness`. If wobbly, raise `Damping` or lower `VelDamp` slightly (try 0.96).
- [ ] **Line-effector test:** duplicate Effector1, set `Shape=Line`, `Behavior=Impulse`, `EndOffset=(400,0)` — expect a clean bar-shaped disturbance with smooth edges.

- [ ] **Commit tuning:**

```bash
git add scripts/WarpGridManager.cs
git commit -m "tune: WarpGrid physics constants"
```

---

## Self-Review Checklist

- [x] §1 **Zero-readback — verified**: runtime `_PhysicsProcess` contains exactly `UploadEffectors → UploadParams → Dispatch → flip`. No `BufferGetData`, no `MeshSurfaceUpdateVertexRegion`, no per-frame CPU-side vertex writes anywhere. Vertex displacement is 100% inside `WarpGridDisplay.gdshader` using `texelFetch(positions_tex, ivec2(VERTEX_ID % W, VERTEX_ID / W), 0).rg`. ✅
- [x] §2 **Line connectivity** — extracted to `MeshHelper.BuildLineGridIndices`: emits all `node[i] → node[i+1]` (horizontal) and all `node[i] → node[i+W]` (vertical). 19,800 segments = 39,600 indices for 100×100. ✅
- [x] §3 **Texture2DRD lifecycle** — confirmed via Context7 (`class_texture2drd`): `Texture2Drd { TextureRdRid = <rid from RenderingDevice.TextureCreate> }` is assigned to the `ShaderMaterial` `positions_tex` parameter. The RD-created image2D stays alive for the life of `WarpGridManager`; freed in `_ExitTree` via `FreeRid(_imgPositions)`. `Texture2DRD` itself is a managed `Resource` wrapper. ✅
- [x] §4 **Pull-only springs** — shader line `if (x < 0.0) return vec2(0.0);` guarantees no force is applied when `current_len < rest_len`. Prevents crunching; creates taut membrane. ✅
- [x] §5 **Buffer roles match spec** — RestBuf: static, anchor force only. StateA/B: ping-pong, pos+vel for Symplectic Euler. PositionsTex: render-only output from compute. ✅
- [x] §6 Line-segment effector via closest-point-on-segment. ✅
- [x] §7 `ComputeListAddBarrier` explicit. ✅
- [x] §8 Tooling: Godot MCP (init, run, debug output), Context7 (RenderingDevice, Texture2DRD, canvas shaders), subagent (Symplectic Euler audit). ✅
- [x] §9 Alignment: State=16, Rest=8, Effector=32, Params std140 padded to 64. ✅
- [x] §10 Impulse CFL cap. ✅

---

## Audit Findings Incorporated (from subagent)

- Symplectic Euler form correct.
- Spring stiffness 0.28 is far below the CFL divergence limit (~3600 for 4-neighbor at dt=1/60).
- Triple damping (spring `c` + rest `c_rest` + global `vel_damp`) is dissipative; risk is over-damping visuals, not instability. Mitigation: vel_damp stays 0.98, not lower.
- **Added impulse force cap** to prevent effectors from blowing past CFL in one frame. No substepping for MVP.
- Snap-to-zero at 1e-4 in normalized space is fine (sub-pixel motion threshold).

---

## Known Gaps / Future Work (Phase 4 roadmap)

- **Anisotropic normalization (P1):** `WarpEffector.ToData` divides `Radius` by `gridSizePixels.X` only, and `WarpGrid.glsl` uses `grid_spacing.x` for `rest_len`. Non-square grids become ellipsoid. Runtime guarded via `GD.PushWarning` in `WarpGridManager._Ready`. Fix: use per-axis normalization or `min(x,y)` consistently.
- **Fixed `Dt = 1/60` (P2):** `_PhysicsProcess` ignores `delta`. Desyncs if `Engine.PhysicsTicksPerSecond` changes. Fix: pass `delta` into UBO.
- **No GPU→CPU channel at runtime (by design):** debugging is visual + RenderDoc capture. We do not introduce any readback path even for dev builds.
- **No adaptive substepping:** if effector strengths are pushed higher than spec, add 2 substeps per frame.
- **Interior anchors:** XNA reference uses a 3×3 loose anchor pattern (k=0.002, c=0.02). Current rest-anchor fills that role uniformly. Add per-cell anchor stiffness if "floaty" — needs a third float per RestState entry (16 bytes/rest).
- **Single-surface mesh:** if line count explodes, consider instanced rendering or a larger mesh batched on GPU.
- **No runtime grid resize:** uniform sets embed RIDs. Resizing requires full teardown + re-init. Acceptable for Phase 3 scope.
- **C# build gate:** first launch requires user to click Build → Build Solution in Godot editor to generate `.csproj`/`.sln`. Headless `--build-solutions` does not trigger generation when no csproj exists.
