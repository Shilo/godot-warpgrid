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
| `RestDamping` | 1.2 | Per-node velocity drag near rest; kills residual tail oscillation. Phase 5 raised from 0.10 for snap-and-settle. | Longer tail oscillation; ripples linger. |
| `VelDamp` | 0.85 | Global per-tick velocity multiplier. At 0.85, ~99.99% decay in 1 s — crisp settle. | `0.92` rings longer; `0.98` feels floaty/underwater. |
| `RestLenScale` | 0.95 | Rest-spring length as fraction of initial spacing. 0.95 = 5% tension baked in. | 1.0 = slack grid (no baseline tension); < 0.9 = visibly pre-stretched. |
| `ImpulseCap` | 0.5 | Max one-frame velocity injection from an impulse effector (normalized units). | Lower = safer; higher = risk of CFL overshoot. |
| `FalloffScale` | 500.0 | Effector near-field sharpness. Falloff `1/(FalloffScale·sp² + d²)`; lower = wider influence per unit Strength. Phase 5 tightened from 1000. | Higher = tighter hot-spot; need ~2× Strength for same visible dent. |
| `glow_gain` (display shader) | 8.0 | Velocity→brightness multiplier. Phase 5 dropped from 60 after damping tightened; residual speed is lower, so gain must rise relative to motion. | Raise if physics gets looser; lower if highlights blow out. |

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
- Fixed `Dt = 1/60` shader constant — accumulator runs the compute at 60 Hz regardless of `Engine.PhysicsTicksPerSecond`, but the shader itself assumes that step size. True variable-`dt` requires UBO plumbing.
- `MaxEffectors = 128` — hard cap. Exceeding it silently drops tail effectors.

## What changed in Phase 5 / 5.1

- `GridW` / `GridH` now count **cells**, not nodes. `GridW=10` → 10 cells across → 11 vertices per row. See spec §15.
- Runtime resize: setting `GridW` / `GridH` / `GridSizePixels` from the Inspector or code triggers `Rebuild()` (teardown + re-init). No restart needed.
- Per-cell rest-anchor weighting via the `protected virtual float RestAnchorWeight(int x, int y)` hook — override on a `WarpGridManager` subclass + call `Rebuild()` to bake a softness map (e.g. 2× near edges, 0.5× in the interior).
- Effector visibility gate — `WarpEffector` with `Visible == false` contributes zero force (skipped in `UploadEffectors`).
- Fixed-step accumulator — compute dispatches at a strict 60 Hz regardless of engine physics tick rate; catch-up capped at 8 steps to avoid spiral-of-death.

## License

Pending.
