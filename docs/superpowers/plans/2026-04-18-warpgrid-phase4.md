# WarpGrid Phase 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden the WarpGrid prototype into a reusable library: decouple effectors via groups, fix anisotropic distortion on non-square grids, scale effector capacity, add velocity-based visual glow, and document tuning.

**Architecture:** Effectors self-register into a Godot group (`warp_effectors`); the manager fetches them each physics tick and does AABB culling against grid world bounds before GPU upload. A new `grid_aspect` UBO field carries `(pixel_w, pixel_h) / min(pixel_w, pixel_h)` so the compute shader can multiply delta vectors before `length()` — restoring circular effector falloff on rectangular grids. Spring rest-length splits into per-axis values (`spacing.x * scale` for horizontal neighbors, `spacing.y * scale` for vertical). The `positions_tex` storage image upgrades from `rg32f` to `rgba32f` so it carries `(pos.xy, vel.xy)`, letting the canvas display shader read velocity magnitude and brighten line color proportionally. Max effector count bumps from 32 to 128.

**Tech Stack:** Godot 4.6.2-mono, C#/.NET, GLSL compute (SPIR-V), D3D12/Forward Mobile, RenderingDevice, Texture2DRD, std430 SSBOs, std140 UBOs.

**Prerequisites:** Phase 3 shipped (commit `56b9668`) and binding-1 SSBO reflection bug fixed (commit `d2b74b6`). Grid renders without errors on square test scene.

**Verification strategy:** GPU shader code cannot be unit-tested in the traditional sense inside Godot. Verification per task is a combination of:
1. `mcp__godot__run_project` followed by `mcp__godot__get_debug_output` — confirms clean boot (no compile/reflection/validation errors).
2. Log inspection at `C:\Users\shilo\AppData\Roaming\Godot\app_userdata\Godot WarpGrid Test\logs\godot.log` — confirms no RD errors over the first few frames.
3. Visual inspection (user gate) — confirms the intended behavior (circular falloff on rectangular grid, velocity brightening, effectors responding at runtime).
4. C# `Debug.Assert` + `GD.PushWarning` for sizes and invariants that can be checked deterministically.

---

## File Structure

| File | Change | Responsibility |
|------|--------|----------------|
| `scripts/WarpEffector.cs` | Modify | Add group registration in `_Ready`/`_ExitTree`. Change `ToData` to normalize radius by `min(gridW, gridH)`. |
| `scripts/WarpGridManager.cs` | Modify | Remove `Effectors` NodePath array. Replace with `GetTree().GetNodesInGroup("warp_effectors")`. Add AABB cull. Bump `MaxEffectors` to 128. Add `_aspect` computation + UBO write. Change texture format to `R32G32B32A32Sfloat`. |
| `shaders/WarpGrid.glsl` | Modify | Add `vec2 grid_aspect` to UBO. Use aspect in effector distance calc. Split spring rest_len into per-axis. Store velocity into `positions_tex.ba`. |
| `shaders/WarpGridDisplay.gdshader` | Modify | Sample 4-channel texture, extract velocity, brighten line color by `length(velocity)`. |
| `README.md` | Create | Tuning guide: Stiffness vs RestStiffness vs Damping interaction; effector strength guidance; common failure modes. |
| `scenes/warpgrid_test.tscn` | Modify | Add Effector2 (line-shaped) + set Effector1 to moving test pattern (optional decoration only). |

---

## Task 1: Effector auto-registration via group

**Files:**
- Modify: `scripts/WarpEffector.cs`

- [ ] **Step 1: Add group constant + registration lifecycle**

Replace the full contents of `scripts/WarpEffector.cs`:

```csharp
using Godot;

namespace WarpGrid;

[GlobalClass]
public partial class WarpEffector : Node2D
{
    public const string Group = "warp_effectors";

    [Export] public WarpShapeType Shape = WarpShapeType.Radial;
    [Export] public WarpBehaviorType Behavior = WarpBehaviorType.Force;
    [Export] public float Radius = 300.0f;
    [Export] public float Strength = 0.01f;
    [Export] public Vector2 EndOffset = Vector2.Zero;

    public override void _Ready()
    {
        AddToGroup(Group);
    }

    public override void _ExitTree()
    {
        if (IsInGroup(Group)) RemoveFromGroup(Group);
    }

    public WarpEffectorData ToData(Vector2 gridOrigin, Vector2 gridSizePixels)
    {
        float minDim = Mathf.Min(gridSizePixels.X, gridSizePixels.Y);
        var startPx = GlobalPosition - gridOrigin;
        var endPx   = startPx + EndOffset;
        return new WarpEffectorData
        {
            StartPoint   = new Vector2(startPx.X / gridSizePixels.X, startPx.Y / gridSizePixels.Y),
            EndPoint     = new Vector2(endPx.X   / gridSizePixels.X, endPx.Y   / gridSizePixels.Y),
            Radius       = Radius / minDim,
            Strength     = Strength,
            ShapeType    = (uint)Shape,
            BehaviorType = (uint)Behavior,
        };
    }
}
```

Key changes vs Phase 3:
- `_Ready` adds to group `"warp_effectors"`.
- `_ExitTree` removes from group with `IsInGroup` guard (safe under tree-exit races).
- `Radius` normalizes by `min(gridW, gridH)` — pixel radius maps to a circle, not an ellipse, on rectangular grids.

- [ ] **Step 2: Run project to verify no compile errors**

Invoke:
```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
```
Wait 5 s, then:
```
mcp__godot__get_debug_output
```
Expected: clean boot (only the `Godot Engine` banner + `D3D12` line, no `ERROR:` lines).
Then `mcp__godot__stop_project`.

- [ ] **Step 3: Commit**

```bash
git add scripts/WarpEffector.cs
git commit -m "feat: WarpEffector self-registers in warp_effectors group + radius uses min-dim"
```

---

## Task 2: Group-based fetching + AABB culling in manager

**Files:**
- Modify: `scripts/WarpGridManager.cs` (replace `Effectors` export + rewrite `UploadEffectors`)

- [ ] **Step 1: Remove `Effectors` NodePath array export**

Replace this block near the top of `scripts/WarpGridManager.cs`:

```csharp
    [Export] public Godot.Collections.Array<NodePath> Effectors = new();
```

with:

```csharp
    // Effectors self-register into the "warp_effectors" group (see WarpEffector.Group).
    // The manager fetches them each physics tick; no manual wiring via Inspector.
```

- [ ] **Step 2: Rewrite `UploadEffectors` to use group fetch + AABB cull**

Replace the existing `UploadEffectors` method body with:

```csharp
    void UploadEffectors()
    {
        // std430 SSBO layout — order-critical, must match WarpEffectorData struct in WarpGrid.glsl.
        Array.Clear(_effScratch, 0, _effScratch.Length);
        int count = 0;
        var gridOrigin = GlobalPosition;
        var gridMin = gridOrigin;
        var gridMax = gridOrigin + GridSizePixels;

        using var ms = new MemoryStream(_effScratch);
        using var bw = new BinaryWriter(ms);
        foreach (Node n in GetTree().GetNodesInGroup(WarpEffector.Group))
        {
            if (count >= MaxEffectors) break;
            if (n is not WarpEffector eff) continue;

            // AABB cull: skip effectors whose influence box can't touch the grid.
            var p = eff.GlobalPosition;
            float r = eff.Radius;
            if (p.X + r < gridMin.X || p.X - r > gridMax.X) continue;
            if (p.Y + r < gridMin.Y || p.Y - r > gridMax.Y) continue;

            var d = eff.ToData(gridOrigin, GridSizePixels);
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

- [ ] **Step 3: Remove Effectors reference from test scene**

Modify `scenes/warpgrid_test.tscn` — delete the line `Effectors = [NodePath("Effector1")]` (the manager no longer uses it). The `Effector1` child node stays as-is; it auto-registers now.

Read the file with `Read` tool first, then `Edit`:
- old_string: `Effectors = [NodePath("Effector1")]`
- new_string: (empty — delete the line, leaving just `script = ExtResource("1_mgr")`)

- [ ] **Step 4: Run project to verify group-based pipeline works**

```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
```
Wait 5 s, `mcp__godot__get_debug_output`.
Expected: clean boot, no errors. Effector1 should still produce a visible dent (Strength=0.01, centered on grid).
Then `mcp__godot__stop_project`.

- [ ] **Step 5: Commit**

```bash
git add scripts/WarpGridManager.cs scenes/warpgrid_test.tscn
git commit -m "feat: manager uses warp_effectors group + AABB cull (drops Effectors NodePath array)"
```

---

## Task 3: Grid aspect UBO field + constant scaling

**Files:**
- Modify: `scripts/WarpGridManager.cs` (UBO, MaxEffectors constant)
- Modify: `shaders/WarpGrid.glsl` (UBO declaration only — shader math in Tasks 4 and 5)

- [ ] **Step 1: Add `grid_aspect` field to shader UBO**

In `shaders/WarpGrid.glsl`, locate the `GridParams` UBO block (around lines 22-35) and replace it with:

```glsl
layout(set = 0, binding = 4, std140) uniform GridParams {
    uvec2 grid_size;         // offset 0
    vec2  grid_spacing;      // offset 8
    float dt;                // offset 16
    float stiffness;         // offset 20
    float damping;           // offset 24
    float rest_stiffness;    // offset 28
    float rest_damping;      // offset 32
    float vel_damp;          // offset 36
    uint  effector_count;    // offset 40
    float rest_length_scale; // offset 44
    float impulse_cap;       // offset 48
    float _pad0;             // offset 52 (std140 padding to align vec2 at 56)
    vec2  grid_aspect;       // offset 56 — (pixel_w, pixel_h) / min(pixel_w, pixel_h)
} p;
```

The block stays 64 bytes total. `vec2` requires 8-byte alignment under std140, so `grid_aspect` must sit at offset 56 (next 8-byte boundary after the float at 48).

- [ ] **Step 2: Bump `MaxEffectors` and update UBO writer**

In `scripts/WarpGridManager.cs`, change:

```csharp
    const int   MaxEffectors  = 32;
```

to:

```csharp
    const int   MaxEffectors  = 128;
```

Then replace the `UploadParams` method body with:

```csharp
    void UploadParams()
    {
        // std140 UBO layout — order-critical, must match GridParams block in WarpGrid.glsl.
        // Block is 64 bytes; 60 meaningful bytes written, remaining 4 left zeroed as padding.
        Array.Clear(_paramScratch, 0, _paramScratch.Length);
        using var ms = new MemoryStream(_paramScratch);
        using var bw = new BinaryWriter(ms);
        bw.Write((uint)GridW); bw.Write((uint)GridH);
        float sx = 1.0f / (GridW - 1);
        float sy = 1.0f / (GridH - 1);
        bw.Write(sx); bw.Write(sy);
        bw.Write(Dt);
        bw.Write(Stiffness);
        bw.Write(Damping);
        bw.Write(RestStiffness);
        bw.Write(RestDamping);
        bw.Write(VelDamp);
        bw.Write(_effCount);
        bw.Write(RestLenScale);
        bw.Write(ImpulseCap);
        bw.Write(0.0f); // _pad0 at offset 52 (std140 alignment for vec2 at 56)
        float minDim = Mathf.Min(GridSizePixels.X, GridSizePixels.Y);
        bw.Write(GridSizePixels.X / minDim); // grid_aspect.x at offset 56
        bw.Write(GridSizePixels.Y / minDim); // grid_aspect.y at offset 60
        _rd.BufferUpdate(_bufParams, 0, (uint)_paramScratch.Length, _paramScratch);
    }
```

- [ ] **Step 3: Remove the non-square warning from `_Ready`**

The warning at `_Ready` was a Phase 3 stopgap. Now that the shader handles anisotropy, delete these lines from `WarpGridManager._Ready`:

```csharp
        if (GridW != GridH || Mathf.Abs(GridSizePixels.X - GridSizePixels.Y) > 0.001f)
            GD.PushWarning($"WarpGridManager: non-square grid ({GridW}x{GridH} @ {GridSizePixels}) " +
                           "will produce anisotropic effector radius + spring rest_len. " +
                           "Use square grid until Phase 4 adds per-axis normalization.");
```

- [ ] **Step 4: Run to confirm UBO upload still valid (shader math unchanged yet, but UBO layout must match)**

```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
mcp__godot__get_debug_output
```
Expected: clean boot. Grid visually unchanged (shader doesn't use `grid_aspect` yet — it's just parked in the UBO).
`mcp__godot__stop_project`.

- [ ] **Step 5: Commit**

```bash
git add scripts/WarpGridManager.cs shaders/WarpGrid.glsl
git commit -m "feat: add grid_aspect to UBO + bump MaxEffectors to 128"
```

---

## Task 4: Shader aspect-corrected effector distance

**Files:**
- Modify: `shaders/WarpGrid.glsl` (`effector_force` function only)

- [ ] **Step 1: Rewrite `effector_force` to use `grid_aspect`**

In `shaders/WarpGrid.glsl`, replace the entire `effector_force` function (lines ~62-87) with:

```glsl
vec2 effector_force(vec2 node_pos, WarpEffectorData e) {
    vec2 center = (e.shape_type == 1u)
        ? closest_on_segment(e.start_point, e.end_point, node_pos)
        : e.start_point;

    // Aspect-corrected delta: multiply by grid_aspect so a pixel-defined radius
    // maps to a true circle (not an ellipse) on rectangular grids.
    // grid_aspect = pixel_size / min(pixel_size), so the short axis stays 1.0
    // and the long axis scales up. Radius was normalized by min-dim in WarpEffector.ToData,
    // so comparing |corrected_delta| against e.radius is a consistent metric.
    vec2  d_raw = node_pos - center;
    vec2  d     = d_raw * p.grid_aspect;
    float d2    = dot(d, d);
    if (d2 > e.radius * e.radius) return vec2(0.0);

    // Force magnitude still driven by corrected distance; direction vector uses raw delta
    // so the push acts along the geometric line from center to node (not the stretched one).
    if (e.shape_type == 0u) {
        vec2 dir_vec = e.end_point - e.start_point;
        if (dot(dir_vec, dir_vec) > 1e-10) {
            // Radial-Directed
            float dist = sqrt(d2);
            return 1.0 * e.strength / (10.0 * p.grid_spacing.x + dist) * normalize(dir_vec);
        }
        // Radial-Explosive — push outward along raw delta; magnitude falls off with corrected distance.
        float denom = 10000.0 * p.grid_spacing.x * p.grid_spacing.x + d2;
        return 2.5 * e.strength * d_raw / denom;
    }
    // Line-Explosive
    float denom = 10000.0 * p.grid_spacing.x * p.grid_spacing.x + d2;
    return 2.5 * e.strength * d_raw / denom;
}
```

Key change: `d` (aspect-corrected) is used for the distance check and denominator; `d_raw` (geometric) is used for the force direction so nodes push outward in the correct physical direction.

- [ ] **Step 2: Run to verify square grid still behaves correctly (aspect = (1,1) → no change)**

```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
mcp__godot__get_debug_output
```
Expected: clean boot. On the square 1000x1000 test scene `grid_aspect = (1, 1)`, so `d == d_raw` and visible behavior matches Phase 3 (centered radial dimple).
`mcp__godot__stop_project`.

- [ ] **Step 3: Commit**

```bash
git add shaders/WarpGrid.glsl
git commit -m "feat: aspect-corrected effector distance in compute shader"
```

---

## Task 5: Shader per-axis spring rest length

**Files:**
- Modify: `shaders/WarpGrid.glsl` (`main()` spring section)

- [ ] **Step 1: Replace uniform `rest_len` with per-axis variants**

In `shaders/WarpGrid.glsl`, locate the spring section inside `main()` (lines ~106-128). Replace the block starting from `float rest_len = ...` through the four neighbor blocks with:

```glsl
    vec2 force = vec2(0.0);
    float rest_len_x = p.grid_spacing.x * p.rest_length_scale;
    float rest_len_y = p.grid_spacing.y * p.rest_length_scale;

    if (c.x > 0u) {
        NodeState n = r_in.data[idx(uvec2(c.x - 1u, c.y))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_x, p.stiffness, p.damping);
    }
    if (c.x + 1u < p.grid_size.x) {
        NodeState n = r_in.data[idx(uvec2(c.x + 1u, c.y))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_x, p.stiffness, p.damping);
    }
    if (c.y > 0u) {
        NodeState n = r_in.data[idx(uvec2(c.x, c.y - 1u))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_y, p.stiffness, p.damping);
    }
    if (c.y + 1u < p.grid_size.y) {
        NodeState n = r_in.data[idx(uvec2(c.x, c.y + 1u))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_y, p.stiffness, p.damping);
    }
```

Horizontal neighbors (±x) use `rest_len_x`, vertical neighbors (±y) use `rest_len_y`. On square grids they're equal (behavior unchanged); on rectangular grids the rest membrane is anisotropically-spaced but each spring sits at its own natural length.

- [ ] **Step 2: Run to confirm square-grid baseline unchanged**

```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
mcp__godot__get_debug_output
```
Expected: clean boot, visually identical to Task 4 (square grid → `rest_len_x == rest_len_y`).
`mcp__godot__stop_project`.

- [ ] **Step 3: Commit**

```bash
git add shaders/WarpGrid.glsl
git commit -m "feat: per-axis spring rest length in compute shader"
```

---

## Task 6: Rectangular grid smoke test

**Files:**
- Modify: `scenes/warpgrid_test.tscn` (temporary aspect override to exercise Phase 4 math)

- [ ] **Step 1: Temporarily reshape the test grid to 16:9**

Read `scenes/warpgrid_test.tscn` first, then modify the `WarpGridManager` node to use a wide-aspect grid. Edit to add these properties to the manager node (under `script = ExtResource("1_mgr")`):

```
GridW = 160
GridH = 90
GridSizePixels = Vector2(1600, 900)
```

Also move `Camera2D` position from `(500, 500)` to `(800, 450)` and `Effector1` from `(500, 500)` to `(800, 450)` so the effector stays centered on the new grid.

Complete updated scene file:

```
[gd_scene format=3 uid="uid://d2k172751ykvi"]

[ext_resource type="Script" uid="uid://corlwgia51h2m" path="res://scripts/WarpGridManager.cs" id="1_mgr"]
[ext_resource type="Script" uid="uid://nby2fir7likb" path="res://scripts/WarpEffector.cs" id="2_eff"]

[node name="WarpGridRoot" type="Node2D" unique_id=1198507561]

[node name="WarpGridManager" type="Node2D" parent="." unique_id=110895201]
script = ExtResource("1_mgr")
GridW = 160
GridH = 90
GridSizePixels = Vector2(1600, 900)

[node name="Effector1" type="Node2D" parent="WarpGridManager" unique_id=1992371321]
visible = false
position = Vector2(800, 450)
script = ExtResource("2_eff")
Strength = 0.01

[node name="Camera2D" type="Camera2D" parent="." unique_id=1388231658]
position = Vector2(800, 450)
```

- [ ] **Step 2: Run + visually verify circular falloff**

```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
mcp__godot__get_debug_output
```
Expected: clean boot on 160x90 @ 1600x900. Effector dent should appear as a **circle**, not a horizontally-stretched ellipse. If dent is oval, aspect math is wrong — re-check `grid_aspect` signs and the `d_raw` vs `d` split in Task 4.
`mcp__godot__stop_project`.

- [ ] **Step 3: Revert scene to square for rest of plan (keep smoke-test commit)**

Edit `scenes/warpgrid_test.tscn` back to square:

```
[gd_scene format=3 uid="uid://d2k172751ykvi"]

[ext_resource type="Script" uid="uid://corlwgia51h2m" path="res://scripts/WarpGridManager.cs" id="1_mgr"]
[ext_resource type="Script" uid="uid://nby2fir7likb" path="res://scripts/WarpEffector.cs" id="2_eff"]

[node name="WarpGridRoot" type="Node2D" unique_id=1198507561]

[node name="WarpGridManager" type="Node2D" parent="." unique_id=110895201]
script = ExtResource("1_mgr")

[node name="Effector1" type="Node2D" parent="WarpGridManager" unique_id=1992371321]
visible = false
position = Vector2(500, 500)
script = ExtResource("2_eff")
Strength = 0.01

[node name="Camera2D" type="Camera2D" parent="." unique_id=1388231658]
position = Vector2(500, 500)
```

- [ ] **Step 4: Commit (single commit captures the verification + revert as one logical unit)**

```bash
git add scenes/warpgrid_test.tscn
git commit -m "test: verified circular falloff on 16:9 grid; reverted scene to square baseline"
```

---

## Task 7: Extend positions_tex to rgba32f (carry velocity)

**Files:**
- Modify: `scripts/WarpGridManager.cs` (texture format)
- Modify: `shaders/WarpGrid.glsl` (image format qualifier + imageStore payload)

- [ ] **Step 1: Change texture format to R32G32B32A32Sfloat**

In `scripts/WarpGridManager.cs`, locate the `RDTextureFormat` block in `InitGpu`:

```csharp
        var fmt = new RDTextureFormat
        {
            Width       = (uint)GridW,
            Height      = (uint)GridH,
            Format      = RenderingDevice.DataFormat.R32G32Sfloat,
            UsageBits   = RenderingDevice.TextureUsageBits.StorageBit
                        | RenderingDevice.TextureUsageBits.SamplingBit
                        | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            TextureType = RenderingDevice.TextureType.Type2D,
        };
```

Replace `R32G32Sfloat` with `R32G32B32A32Sfloat`:

```csharp
        var fmt = new RDTextureFormat
        {
            Width       = (uint)GridW,
            Height      = (uint)GridH,
            Format      = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            UsageBits   = RenderingDevice.TextureUsageBits.StorageBit
                        | RenderingDevice.TextureUsageBits.SamplingBit
                        | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            TextureType = RenderingDevice.TextureType.Type2D,
        };
```

- [ ] **Step 2: Update shader image format + writes**

In `shaders/WarpGrid.glsl`, change the image2D declaration (around line 37):

```glsl
layout(set = 0, binding = 5, rg32f) uniform restrict writeonly image2D positions_tex;
```

to:

```glsl
layout(set = 0, binding = 5, rgba32f) uniform restrict writeonly image2D positions_tex;
```

Then find the two `imageStore` calls:

1. Inside the boundary-node branch (around line 102):
   ```glsl
       imageStore(positions_tex, ivec2(c), vec4(rest, 0.0, 0.0));
   ```
   Leave as-is — boundary nodes have zero velocity, so `.ba = (0,0)` is correct.

2. At end of `main()` (around line 157):
   ```glsl
       imageStore(positions_tex, ivec2(c), vec4(new_pos, 0.0, 0.0));
   ```
   Replace with:
   ```glsl
       imageStore(positions_tex, ivec2(c), vec4(new_pos, new_vel));
   ```

- [ ] **Step 3: Run to verify format change doesn't break dispatch**

```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
mcp__godot__get_debug_output
```
Expected: clean boot. Visually unchanged (display shader still only reads `.rg`).
`mcp__godot__stop_project`.

- [ ] **Step 4: Commit**

```bash
git add scripts/WarpGridManager.cs shaders/WarpGrid.glsl
git commit -m "feat: positions_tex carries velocity in .ba (rgba32f)"
```

---

## Task 8: Display shader velocity glow

**Files:**
- Modify: `shaders/WarpGridDisplay.gdshader`

- [ ] **Step 1: Rewrite display shader to brighten by velocity**

Read `shaders/WarpGridDisplay.gdshader` first to preserve its existing structure, then replace the entire file with:

```glsl
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_tex : filter_nearest;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
uniform vec4 line_color : source_color = vec4(0.15, 0.55, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 200.0) = 60.0;

void vertex() {
    int w = grid_dims.x;
    int id = int(VERTEX_ID);
    ivec2 c = ivec2(id - (id / w) * w, id / w);
    vec4  s = texelFetch(positions_tex, c, 0);
    vec2  p = s.rg;
    vec2  v = s.ba;
    VERTEX = p * grid_size_pixels;

    // Brightness boost proportional to velocity magnitude.
    // glow_gain controls how fast lines go from base color toward white.
    float speed = length(v);
    float glow  = clamp(speed * glow_gain, 0.0, 1.0);
    COLOR = mix(line_color, vec4(1.0, 1.0, 1.0, line_color.a), glow);
}
```

Key additions:
- `glow_gain` uniform (default 60) — tuning knob for how aggressively lines brighten.
- Velocity sampled from `.ba` channels of `positions_tex`.
- `COLOR` is vertex output → interpolated along each line segment, so moving regions glow and resting regions stay base color.

- [ ] **Step 2: Run + visually verify glow**

```
mcp__godot__run_project projectPath="C:\Programming_Files\Shilocity\godot-warpgrid" scene="res://scenes/warpgrid_test.tscn"
mcp__godot__get_debug_output
```
Expected: clean boot. Effector stays at center (Strength=0.01). To see glow, move Effector1 in the editor while running, or raise Strength temporarily. Moving areas should brighten toward white; static areas stay blue.
`mcp__godot__stop_project`.

- [ ] **Step 3: Commit**

```bash
git add shaders/WarpGridDisplay.gdshader
git commit -m "feat: velocity-proportional glow in display shader"
```

---

## Task 9: README tuning guide

**Files:**
- Create: `README.md`

- [ ] **Step 1: Create README with tuning guide**

Create `README.md` at the repo root with this content:

````markdown
# WarpGrid

GPU-driven reactive mass-spring grid for Godot 4.6 mono. 100×100 nodes simulated on a single compute dispatch per physics tick, rendered as line segments via a `Texture2DRD` bridge — zero per-frame CPU↔GPU transfer beyond the small effector + params upload.

Visual target: the taut elastic membrane of *Geometry Wars: Retro Evolved*. Ripples propagate, reflect off anchored edges, carry momentum, decay over seconds rather than frames. Effectors (game entities: bullets, explosions, player) deform the grid via radial or line-segment forces.

## Quick start

1. Open the project in Godot 4.6.2-mono (Windows / d3d12 / Forward Mobile renderer).
2. Editor → Build → Build Solution (C# .NET 8 required).
3. F5 to launch the test scene `scenes/warpgrid_test.tscn`.
4. Drag `Effector1` in the remote tree at runtime — ripples should follow the cursor and reflect off the anchored perimeter.

## Architecture at a glance

- `scripts/WarpGridManager.cs` — RenderingDevice lifecycle, compute dispatch, ping-pong, UBO + SSBO uploads.
- `scripts/WarpEffector.cs` — Node2D that self-registers into the `warp_effectors` group. Manager AABB-culls per frame.
- `scripts/WarpEffectorData.cs` + `scripts/MeshHelper.cs` — GPU struct mirror + static mesh topology.
- `shaders/WarpGrid.glsl` — compute kernel (symplectic Euler + pull-only Hooke's + effector forces + boundary anchoring).
- `shaders/WarpGridDisplay.gdshader` — canvas_item vertex shader that `texelFetch`es the compute output and brightens by velocity.

See `docs/superpowers/specs/warpgrid-gpgpu-design-spec.md` for the full design.

## Tuning guide

The grid lives in normalized [0, 1] space regardless of pixel size. All physics constants in `WarpGridManager.cs` are tuned for that range. If you change grid dimensions or want a different feel, adjust these in tandem:

### The three forces

Every node receives three kinds of force every tick:

| Source | Constants | Role |
|--------|-----------|------|
| Neighbor springs | `Stiffness`, `Damping` | Keeps the membrane taut; propagates ripples outward. |
| Rest anchor | `RestStiffness`, `RestDamping` | Pulls each node back to its resting position. Prevents permanent deformation. |
| Effectors | Per-effector `Strength`, `Radius` | External disturbances from game entities. |

### Tuning knobs

| Constant | Default | Effect when raised | Effect when lowered |
|----------|---------|--------------------|---------------------|
| `Stiffness` | 10.0 | Crisper ripples, higher wave speed. > ~900 violates CFL (instability). | Mushier, slower ripples. At 0.28 (Phase 3 starting point) it felt "underwater." |
| `Damping` | 0.45 | Smoother neighbor response; kills high-frequency "crunch." | Crunchier but more energetic; too low = visual noise. |
| `RestStiffness` | 6.0 | Snaps back to rest faster; less ringing. | Slower snapback; ripples linger. 0 = no rest pull (nodes drift). |
| `RestDamping` | 0.10 | Per-node velocity drag when near rest; reduces final oscillation. | Longer tail oscillation. |
| `VelDamp` | 0.92 | Global per-tick velocity multiplier. At 0.92, ~99% decay in 1 s. | `0.98` feels floaty/underwater; `0.85` feels twitchy. |
| `RestLenScale` | 0.95 | Rest-spring length as fraction of initial spacing. 0.95 = 5% tension baked in. | 1.0 = slack grid (no baseline tension); < 0.9 = visibly pre-stretched. |
| `ImpulseCap` | 0.5 | Max one-frame velocity injection from an impulse effector (normalized units). | Lower = safer; higher = risk of CFL overshoot. |

### Effector tuning

`WarpEffector.Strength` is multiplicative against the falloff formula. For 100×100 grid + Radius 300 px, `Strength = 0.01` gives a gentle dent. Scale:

| Strength | Feel |
|----------|------|
| 0.001 | Barely visible — good for ambient sway. |
| 0.01 | Player position / cursor tracker — gentle ripple wake. |
| 0.1 | Bullet impact — visible dent, quick recovery. |
| 1.0 | Explosion — large dent, multi-second ripple. |

`Radius` is in pixels and normalizes by the shorter grid dimension, so circular falloff is preserved on rectangular grids.

### Common failures

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Grid explodes / NaN after a few seconds | `Stiffness` near CFL limit (~900) or `Dt` too large | Halve `Stiffness` or lower `ImpulseCap`. |
| Grid never settles | `VelDamp` too close to 1.0 or `RestStiffness` too low | Raise `RestStiffness` or lower `VelDamp`. |
| Ripples feel "underwater" | `Stiffness` too low for normalized space | Raise `Stiffness` to 8–12 range. |
| No visible reaction to effector | `Strength` too low, or effector outside grid AABB | Raise `Strength` 10× or check effector world position. |
| Stretched-ellipse falloff on wide grids | You're running Phase 3 without the aspect fix | Pull latest (Phase 4 Task 4 + 6). |

## Known limitations

- Single grid instance per scene — multi-grid layering would need separate RD resources per manager.
- Uniform rest-anchor stiffness across the grid — interior "soft spots" not supported yet.
- Fixed `Dt = 1/60` — changing `Engine.PhysicsTicksPerSecond` at runtime requires a restart.
- `MaxEffectors = 128` — hard cap. Exceeding it silently drops tail effectors.

## License

Pending.
````

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: add README with tuning guide"
```

---

## Task 10: Update design spec + close Phase 4

**Files:**
- Modify: `docs/superpowers/specs/warpgrid-gpgpu-design-spec.md`

- [ ] **Step 1: Patch spec §5.4 (UBO table) + §5.5 (bindings) + §6 (effector math) + §10 (files) + §11 (limitations) + §14 (future direction)**

Read `docs/superpowers/specs/warpgrid-gpgpu-design-spec.md` first.

Replace §5.4 GridParams UBO table with:

```markdown
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
| 52 | _pad0 | float | 0 (std140 alignment before vec2 at 56) |
| 56 | grid_aspect | vec2 | `(pixel_w, pixel_h) / min(pixel_w, pixel_h)` — corrects anisotropic distance |
```

In §6.2, replace the "Effectors" paragraph with:

```markdown
**Effectors:** Variable-strength radial falloff. Center is `start_point` (Radial) or closest-point-on-segment (Line). Delta is aspect-corrected — `d = (node_pos - center) * grid_aspect` — so a pixel-defined radius (normalized by the grid's shorter pixel dimension) maps to a true circle on rectangular grids. Falloff denominators use `d2 = dot(d, d)`; force direction uses the raw (non-corrected) delta so push vectors stay geometrically correct.
- **Radial explosive** (`shape==0, start==end`): `2.5·S·d_raw / (10000·sp² + |d|²)`
- **Radial directed** (`shape==0, start≠end`): `1.0·S / (10·sp + |d|) · normalize(end-start)`
- **Line explosive** (`shape==1`): `2.5·S·d_raw / (10000·sp² + |d|²)` with center = closest point on segment.
```

In §6.2, update the "Neighbor springs" paragraph's rest length discussion — at the end, append:

```markdown

**Per-axis rest length:** Horizontal neighbors (`±x`) use `rest_len_x = grid_spacing.x * rest_length_scale`; vertical neighbors (`±y`) use `rest_len_y = grid_spacing.y * rest_length_scale`. On square grids they're equal; on rectangular grids each spring sits at its own natural rest length, avoiding the Phase 3 anisotropic-tension warning.
```

In §7.2, update the fragment/vertex shader snippet to reflect velocity glow:

```markdown
### 7.2 Zero-readback displacement + velocity glow
`WarpGridDisplay.gdshader` (canvas_item):
```glsl
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
```
`positions_tex` is `rgba32f`; compute kernel writes `(pos, vel)` to `.rgba`, display shader reads both. `glow_gain` is a canvas-shader uniform (default 60).
```

In §11, delete limitations #1 (anisotropic on non-square) — now fixed. Renumber remaining items.

In §14, remove "Per-axis normalization fix" from the list — shipped.

Add a new §15 at the end:

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/warpgrid-gpgpu-design-spec.md
git commit -m "docs: spec reflects Phase 4 (aspect fix, velocity glow, group-based effectors)"
```

---

## Self-review checklist (run before execution)

- [x] Spec coverage — all 4 Phase 4 subsystems mapped to tasks:
  - §0 Bug fix → done pre-plan (commit `d2b74b6`)
  - §1A Auto-registration → Task 1
  - §1B Group fetch + AABB → Task 2
  - §2 Per-axis aspect → Tasks 3, 4, 5 + verification in Task 6
  - §3 Scaling (MaxEffectors=128) → Task 3 step 2
  - §3 Velocity glow → Tasks 7 + 8
  - §3 README → Task 9
  - Spec doc update → Task 10
- [x] Placeholder scan — all code blocks complete; no TBDs.
- [x] Type consistency — `WarpEffectorData`, `MaxEffectors`, `grid_aspect`, `positions_tex` used consistently across tasks.
- [x] Verification strategy — every task ends with `mcp__godot__run_project` → `get_debug_output` → visual check; Task 6 is the explicit 16:9 smoke test.
- [x] Commit boundaries — one commit per task (or per logical subtask), each builds and runs cleanly.
