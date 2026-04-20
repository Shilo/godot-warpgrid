# WarpGrid

CPU-driven reactive warp grid for Godot 4.6 mono. A low-resolution mass-spring lattice runs on the CPU each physics tick, then the current node positions are packed into an `RGBA32F` texture where `.xy` is the current position and `.zw` is the anchor. The display shader bilinear-samples that texture to bend a higher-resolution visual mesh, so the GPU now handles interpolation and rendering only.

The visual target is the taut elastic skin from *Geometry Wars: Retro Evolved*: fast spring propagation, strong snapback, and a clean neon wake when effectors move through the field.

## Architecture

- `scripts/WarpGridManager.cs`
  CPU simulation, spring generation, effector injection, texture packing, and mesh/material setup.
- `scripts/WarpGridPoint.cs`
  Packed point state used by the solver.
- `scripts/WarpGridSpring.cs`
  Spring topology metadata for right, up, and original-position springs.
- `scripts/WarpEffector.cs`
  Pixel-space radial or directional force input.
- `scripts/WarpMouseController.cs`
  Runtime/editor cursor driver for quick interaction.
- `scripts/WarpGridGpuManifest.cs`
  Shared CPU↔shader texture packing contract for the positions texture.
- `shaders/WarpGridDisplay.gdshader`
  Bilinear display bridge plus neon shading.

## Runtime model

- Physics lattice: `PhysicsGridW x PhysicsGridH` cells, with one anchor spring per node and cardinal springs across the grid.
- Visual mesh: `GridW x GridH` cells, sampled against the lower-resolution positions texture.
- Integration: semi-implicit Euler at Godot's fixed `_PhysicsProcess` cadence.
- Boundary guard: perimeter nodes hard-pin to anchor when `ClampEdges` is enabled.
- Sleep guard: nodes snap fully to rest once displacement and velocity fall below `1e-4`.

## Main tuning knobs

- `Preset`
  Applies the Geometry Wars-style defaults.
- `SpringStiffness`
  Neighbor spring pull. Higher values make the sheet feel tighter and more immediate.
- `SpringDamping`
  Relative-velocity damping along springs. Higher values calm high-frequency shimmer.
- `GlobalDamping`
  Post-integration velocity decay. Lower values settle faster.
- `AnchorPull`
  Strength of the original-position spring on every node.
- `Mass`
  Inverse response scale for all accumulated forces.
- `ClampEdges`
  Hard-pins the outer ring of nodes to the anchor lattice.

## Interaction

`WarpEffector` stays in pixel space. Force, impulse, vortex, gravity-well, and shockwave behaviors all feed the same CPU solver, while line effectors use the closest point on the segment to produce elongated disturbances. `WarpMouseController` spawns a runtime effector so the grid can be pushed in the editor or in play mode without manual scene wiring.

## Quick start

1. Open the project in Godot 4.6.2 mono.
2. Build the solution from the editor.
3. Run `scenes/warpgrid_test.tscn`.
4. Use the mouse controller or move effectors in the remote tree to deform the grid.

## Notes

- The solver is intentionally CPU-side now; there is no compute-shader physics dispatch loop.
- The shader expects the positions texture contract described in `WarpGridGpuManifest.cs`.
- No automated tests are included for this refactor by design.
