#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// Phase 13 — Scalar Wave model. h_curr/h_prev store scalar height (pixel magnitude);
// dir latches the effector push direction so the display shader can warp in 2D
// without reintroducing vector-spring instability.
struct NodeState       { float h_curr; float h_prev; vec2 dir; };
struct RestState       { vec2 anchor; float weight; float _pad; };
struct WarpEffectorData {
    vec2  start_point;
    vec2  end_point;
    float radius;
    float strength;
    uint  shape_type;
    uint  behavior_type;
};

layout(set = 0, binding = 0, std430) buffer ReadSt   { NodeState data[]; } r_in;
layout(set = 0, binding = 1, std430) buffer WriteSt  { NodeState data[]; } r_out;
layout(set = 0, binding = 2, std430) restrict readonly  buffer RestBuf  { RestState data[]; } r_rest;
layout(set = 0, binding = 3, std430) restrict readonly  buffer EffBuf   { WarpEffectorData data[]; } r_eff;

// 48-byte UBO, std140. anchor_stiffness reuses the former padding slot at offset 40.
layout(set = 0, binding = 4, std140) uniform GridParams {
    uvec2 grid_size;         // offset 0
    vec2  grid_spacing;      // offset 8
    float tension;           // offset 16 — Laplacian coupling (wave speed^2 per step)
    float damping;           // offset 20 — global per-step height decay (<1 dissolves waves)
    float edge_damp;         // offset 24 — sponge multiplier applied to a 3-cell fringe
    float dir_decay;         // offset 28 — direction magnitude persistence per step
    uint  effector_count;    // offset 32
    float force_scaler;      // offset 36 — 1/SubSteps so per-engine-tick energy is invariant
    float anchor_stiffness;  // offset 40 — independent pull toward h=0 for localized ripples
    float _pad2;             // offset 44
} p;

layout(set = 0, binding = 5, rgba32f) uniform restrict writeonly image2D positions_tex;

uint idx(uvec2 c) { return c.y * p.grid_size.x + c.x; }

vec2 closest_on_segment(vec2 a, vec2 b, vec2 p0) {
    vec2  ba = b - a;
    float denom = dot(ba, ba);
    if (denom < 1e-10) return a;
    float t = clamp(dot(p0 - a, ba) / denom, 0.0, 1.0);
    return a + t * ba;
}

void main() {
    uvec2 c = gl_GlobalInvocationID.xy;
    if (c.x >= p.grid_size.x || c.y >= p.grid_size.y) return;

    uint i = idx(c);
    NodeState me = r_in.data[i];
    RestState rs = r_rest.data[i];
    vec2 anchor  = rs.anchor;

    // Edge sponge wall — boundary nodes pinned to h=0, act as perfect absorber.
    if (c.x == 0u || c.y == 0u || c.x == p.grid_size.x - 1u || c.y == p.grid_size.y - 1u) {
        NodeState z;
        z.h_curr = 0.0;
        z.h_prev = 0.0;
        z.dir    = vec2(0.0);
        r_out.data[i] = z;
        imageStore(positions_tex, ivec2(c), vec4(0.0));
        return;
    }

    // 4-neighbor Laplacian: sum heights and directions from cardinal neighbors.
    NodeState nl = r_in.data[idx(uvec2(c.x - 1u, c.y))];
    NodeState nr = r_in.data[idx(uvec2(c.x + 1u, c.y))];
    NodeState nu = r_in.data[idx(uvec2(c.x, c.y - 1u))];
    NodeState nd = r_in.data[idx(uvec2(c.x, c.y + 1u))];
    float h_avg  = (nl.h_curr + nr.h_curr + nu.h_curr + nd.h_curr) * 0.25;
    vec2  d_sum  = nl.dir + nr.dir + nu.dir + nd.dir;

    // Discrete 2D wave equation (leapfrog form) with restoring anchor force:
    //   h_next = 2h - h_prev + tension * (h_avg - h) + (0 - h) * anchor_stiffness
    // Tension = wave speed^2; stable while tension * 4 < 2 (CFL for 4-neighbor Laplacian).
    float anchor_pull = (0.0 - me.h_curr) * p.anchor_stiffness;
    float h_next = 2.0 * me.h_curr - me.h_prev + p.tension * (h_avg - me.h_curr) + anchor_pull;
    h_next *= p.damping;

    // Fringe damping: stronger decay in a 3-cell sponge so waves dissolve instead of reflect.
    uint dx = min(c.x, p.grid_size.x - 1u - c.x);
    uint dy = min(c.y, p.grid_size.y - 1u - c.y);
    uint d_edge = min(dx, dy);
    if (d_edge < 3u) {
        float t = float(d_edge) / 3.0;  // 0 at wall, 1 at the 3rd-in ring
        h_next *= mix(p.edge_damp, 1.0, t);
    }

    // Direction propagation: renormalize the 4-neighbor average; fall back to existing dir.
    vec2  new_dir = me.dir;
    float d_len   = length(d_sum);
    if (d_len > 1e-6) new_dir = d_sum / d_len;
    new_dir *= p.dir_decay;

    // Effectors inject height at the anchor and latch the push direction.
    for (uint e = 0u; e < p.effector_count; e++) {
        WarpEffectorData ed = r_eff.data[e];
        vec2 center = (ed.shape_type == 1u)
            ? closest_on_segment(ed.start_point, ed.end_point, anchor)
            : ed.start_point;
        vec2  d_raw = anchor - center;
        float d2    = dot(d_raw, d_raw);
        if (d2 > ed.radius * ed.radius) continue;

        float sigma = max(ed.radius, 1e-4) * 0.5;
        // Delta-scale: force_scaler = 1/SubSteps keeps total per-engine-tick energy
        // constant whether the kernel dispatches 2× or 8× per physics frame. Without
        // this, stationary effectors pump N× strength per tick and blow the field out.
        float gauss = exp(-d2 / (2.0 * sigma * sigma)) * p.force_scaler;
        float amp   = ed.strength * gauss;

        // Push direction: Radial-Directed uses start→end; others radiate out from center.
        vec2  push_dir = vec2(0.0);
        if (ed.shape_type == 0u) {
            vec2 dv = ed.end_point - ed.start_point;
            if (dot(dv, dv) > 1e-10) {
                push_dir = normalize(dv);
            } else {
                float len = length(d_raw);
                if (len > 1e-6) push_dir = d_raw / len;
            }
        } else {
            float len = length(d_raw);
            if (len > 1e-6) push_dir = d_raw / len;
        }

        if (ed.behavior_type == 1u) {
            // Impulse — set height to the Gaussian peak, overwrite direction.
            h_next  = (abs(amp) > abs(h_next)) ? amp : h_next;
            if (dot(push_dir, push_dir) > 1e-10) new_dir = push_dir;
        } else {
            // Force — accumulate height, blend direction toward push.
            h_next += amp;
            if (dot(push_dir, push_dir) > 1e-10) {
                vec2 blended = mix(new_dir, push_dir, 0.5);
                float bl = length(blended);
                new_dir = (bl > 1e-6) ? (blended / bl) : push_dir;
            }
        }
    }

    // Jitter guard — kill sub-pixel noise so the grid settles back to a dead calm.
    if (abs(h_next) < 1e-2) {
        h_next  = 0.0;
        new_dir = vec2(0.0);
    }

    NodeState result;
    result.h_curr = h_next;
    result.h_prev = me.h_curr;
    result.dir    = new_dir;
    r_out.data[i] = result;

    // Display shader reads: .r = h_curr, .g = h_prev (debug), .ba = dir.
    imageStore(positions_tex, ivec2(c), vec4(h_next, me.h_curr, new_dir.x, new_dir.y));
}
