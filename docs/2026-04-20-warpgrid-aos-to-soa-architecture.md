# WarpGrid Architectural Evolution: AoS to SoA

## Purpose

This document explains the WarpGrid project’s shift from an object-oriented, Array-of-Structures (AoS) spring grid to a data-oriented, Structure-of-Arrays (SoA) CPU solver. It is grounded in the legacy Unity implementation:

- `VectorGrid.cs`
- `VectorGridPoint.cs`
- `VectorGridSpring.cs`

and the current Godot implementation:

- `scripts/WarpGridManager.cs`
- `scripts/WarpGridGpuManifest.cs`
- `diff.patch`

The goal is to clarify the performance wins, stability improvements, and engineering trade-offs behind the new architecture.

## Summary Table

| Pillar | Then: AoS `VectorGrid` | Now: SoA `WarpGrid` |
|---|---|---|
| Core state | `VectorGridPoint[,]` object graph | Flat `float[]` buffers for position, velocity, acceleration, and anchors |
| Spring topology | `VectorGridSpring` objects with `Right`, `Up`, and `OriginalPosition` types | Implicit neighbor topology from direct index math plus per-node anchor pull |
| Timing model | Frame-driven `Update()` loop | Fixed 120 Hz accumulator in `_PhysicsProcess` with adaptive substeps |
| Execution | Single-threaded nested loops | Batched `Parallel.ForEach` range partitioning plus optional effector bucketing |
| Render bridge | CPU rewrites mesh vertices/colors directly | CPU packs textures; GPU bilinear-samples low-res state on a denser display mesh |
| Visual energy | Position/color lives on point objects | Displacement and velocity textures drive glow, taper, persistence, and aberration |
| Scaling | Physics and render density mostly coupled | Physics density decoupled from display mesh density |

## A. Memory Architecture: AoS vs. SoA

The legacy design is AoS. In `VectorGrid.cs`, the simulation is stored as a `VectorGridPoint[,]` grid, and each `VectorGridPoint` owns its own fields for position, original position, velocity, acceleration, damping, color state, and clamp flags. Each point also owns an array of `VectorGridSpring` references. `VectorGridSpring` in turn stores references to two points, spring type, stiffness, damping, and rest distance.

That design is intuitive, but it spreads the simulation state across many heap objects and pointer hops. Updating a spring means touching the spring object, then chasing references to points, then pulling in point-local fields that may not be contiguous in memory. At small sizes this is fine. At larger sizes it becomes increasingly cache-unfriendly.

The current manager in `scripts/WarpGridManager.cs` moves to SoA. Instead of one object per point, state is split into contiguous arrays:

- `_posX`, `_posY`
- `_velX`, `_velY`
- `_accX`, `_accY`
- `_anchorX`, `_anchorY`
- `_edgeMask`, `_sleeping`

Spring topology is no longer stored as spring objects. Horizontal and vertical neighbors are derived from array offsets like `i - 1`, `i + 1`, `i - PhysNodesX`, and `i + PhysNodesX`, while anchor behavior is represented by per-node pull toward `_anchorX/_anchorY`.

### Technical Impact

This is the biggest performance shift in the project. The SoA model improves CPU cache locality because the hot loops stream linearly through tightly packed floats rather than bouncing between objects. That reduces cache misses, improves hardware prefetch behavior, and lowers memory bandwidth waste. It also gives the .NET JIT a much better chance to eliminate bounds checks and auto-vectorize branch-light arithmetic.

The AoS version optimized for authoring convenience. The SoA version optimizes for simulation throughput.

## B. The Physics Integration Kernel

The legacy kernel updates points in a frame-driven style. In `VectorGridPoint.UpdatePositionAndColor()`, velocity is incremented by acceleration, position is incremented by velocity, acceleration is cleared, and velocity damping is applied. This happens from the `Update()` path in `VectorGrid.cs`, so the integration cadence follows render cadence rather than a fixed simulation frequency.

The new solver in `scripts/WarpGridManager.cs` uses a fixed 120 Hz accumulator in `_PhysicsProcess`. Each fixed tick can be subdivided further through `SubSteps`, and the manager applies:

1. Force accumulation
2. Velocity update
3. Damping
4. Position update

This is semi-implicit, or symplectic, Euler. Compared with straightforward explicit Euler, symplectic Euler behaves better for spring systems because it tends to preserve energy more naturally and resists the mushy or exploding feel that springs can develop when timing is inconsistent.

### Timing Stability

Moving from variable render-driven updates to a fixed 120 Hz clock is a stability upgrade as much as a feel upgrade. The wave propagation speed, damping response, and spring snapback no longer vary with monitor refresh or frame pacing. A player at 60 FPS and a player at 144 FPS see the same underlying sheet behavior.

### Arcade Vibe

This combination of taut springs, symplectic Euler, and fixed-rate stepping is what creates the “Geometry Wars” character. The grid snaps back quickly, preserves enough momentum for visible wake and reflection, and avoids the soggy, inconsistent feel that often comes from frame-rate-coupled updates.

## C. Execution and Threading Model

The original Unity-style solver is single-threaded. `VectorGrid.UpdateGrid()` walks the point grid, updates springs, then walks it again to update points. It is conceptually simple and easy to reason about, but it scales linearly on one core and pays the cost of object traversal the whole time.

The current manager uses batched range partitioning with `Parallel.ForEach`. Rather than scheduling per-point work, it creates contiguous ranges of indices and distributes those across threads. The latest `diff.patch` hardens this further by increasing the minimum batch size to `1024`, explicitly reducing context-switching and scheduler overhead for large grids.

The manager also adds optional effector partitioning when active effector count becomes large. Instead of checking every effector against every node, it uses a lightweight strip-based bucket so nodes only inspect nearby effectors.

### Universal Scaling

The architectural scaling story is not just “more threads.” The new system also decouples physics density from render density. `PhysicsGridW/H` controls solver resolution, while `GridW/H` controls visible mesh density. A relatively coarse physics lattice can still render as a smooth, dense silk sheet because the shader interpolates between packed node states.

This is a major scalability upgrade over the old approach, where simulation detail and visible mesh detail were much more tightly linked.

## D. The Rendering Bridge

The legacy bridge updates mesh data directly on the CPU. `VectorGrid.cs` rebuilds or mutates mesh vertices, colors, UVs, and indices in normal engine arrays. This is easy to understand, but it means the CPU owns both deformation and final display geometry.

The current bridge in `scripts/WarpGridManager.cs` is texture-based. The solver writes:

- positions and anchors into an `RGBA32F` texture
- velocity magnitude into a separate velocity texture

`scripts/WarpGridGpuManifest.cs` defines and verifies this packing contract. That file effectively acts as a CPU-GPU ABI for the display bridge.

On the GPU side, `WarpGridDisplay.gdshader` bilinear-samples the low-resolution textures and applies the sampled warp to a denser display mesh. This makes the GPU responsible only for interpolation and shading, not simulation.

### Shading Logic

The display shader derives visual energy from two sources:

- displacement between sampled `position` and `anchor`
- sampled local velocity magnitude

Those signals drive:

- neon glow intensity
- chromatic aberration
- phosphor persistence
- line tapering
- hot-wire color shift

The recent `diff.patch` specifically tightened the overload response so aberration scales with `pow(energy, 2.0)`, which makes high-energy impacts read as much more violent and arcade-like.

## E. Comprehensive Pros and Cons

### Pros

- Much better cache efficiency from contiguous state buffers
- Far lower pointer chasing than the point-and-spring object graph
- Better compatibility with JIT auto-vectorization and bounds-check elimination
- Effectively zero-allocation hot loops through reusable scratch buffers
- Fixed-step simulation makes behavior frame-rate independent
- Batched parallel execution scales across modern CPUs
- Physics density can stay coarse while the render mesh remains fine
- Texture-based bridge enables richer display effects from displacement and velocity

### Cons

- Code is less immediately readable than an object-per-point model
- State synchronization is manual; every new field must stay index-aligned
- Debugging individual nodes is less ergonomic because there is no point object to inspect
- Uploading larger textures every frame can become a bandwidth bottleneck at very high solver resolutions
- The design is less convenient for ad hoc gameplay extensions that want to “just add a field to the point”
- Parallel phases require more discipline around read/write separation than the old single-threaded loops

### About SIMD

The current manager does not use an explicit `System.Runtime.Intrinsics` path. Instead, the hot loops are written as contiguous forward walks over SoA arrays so they remain cache-friendly and shaped in a way the JIT can optimize aggressively. That is a pragmatic middle ground: better data layout first, explicit intrinsics only if profiling later proves they are necessary.

## Developer Recommendation

Use a **coarse physics lattice** when the grid is primarily a visual effect or background reactive surface. This is the best default for arcade shooters, bullet-heavy scenes, and larger arenas. Let the shader and display mesh create smoothness on top of a relatively low-cost simulation.

Use a **fine physics lattice** when deformation fidelity is gameplay-critical, when effectors are tightly localized, or when small-scale wave interactions need to remain physically legible. Increase physics density only after the coarse grid stops preserving the shapes that matter.

The best default for this architecture is:

- coarse physics
- fine display mesh
- fixed 120 Hz simulation
- batched parallel execution

That combination captures the main benefit of the WarpGrid evolution: the solver is no longer optimized for authoring convenience first. It is optimized for stable, scalable, arcade-quality motion.
