#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

struct NodeState       { vec2 position; vec2 velocity; };
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
    float falloff_scale;     // offset 52 — effector near-field sharpness (lower = tighter)
    vec2  grid_aspect;       // offset 56 — (pixel_w, pixel_h) / min(pixel_w, pixel_h)
    float velocity_blend;    // offset 64 — Phase 9 Laplacian mix factor (0 = off, 0.15 = default)
} p;

layout(set = 0, binding = 5, rgba32f) uniform restrict writeonly image2D positions_tex;

uint idx(uvec2 c) { return c.y * p.grid_size.x + c.x; }

vec2 spring_force(vec2 me_pos, vec2 me_vel, vec2 other_pos, vec2 other_vel,
                  float rest_len, float k, float c) {
    // Phase 6.2 guard: rest_len==0 would imply two nodes sharing an anchor (degenerate).
    if (rest_len < 1e-9) return vec2(0.0);
    vec2  delta = other_pos - me_pos;
    float len   = length(delta);
    if (len < 1e-7) return vec2(0.0);
    vec2  dir   = delta / len;
    // Phase 6.6: pull-only springs — compression (x < 0) produces zero force, preventing
    // runaway feedback where overshooting pairs push each other past the no-return point.
    // Phase 7.2: raw pixel stretch — stiffness is tuned at Unity pixel-scale so `k * px` gives
    // an acceleration in pixels-per-step-squared that integrates cleanly.
    float x     = len - rest_len;
    if (x < 0.0) return vec2(0.0);
    vec2  dv    = other_vel - me_vel;
    float f     = k * x - dot(dv, dir) * c;
    return dir * f;
}

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

    // Phase 7: pixel-space — radius, node_pos and center are all in absolute pixels, so
    // a true circle falls out of a plain euclidean distance test. No aspect correction.
    vec2  d_raw = node_pos - center;
    float d2    = dot(d_raw, d_raw);
    if (d2 > e.radius * e.radius) return vec2(0.0);

    // Phase 6.2: Gaussian hump — magnitude is purely a Gaussian of distance, direction is a unit
    //   vector so the force profile is a smooth bubble with a defined peak at the center.
    //   sigma = radius * 0.5 places ~0.61× peak at half-radius, ~0.14× peak at the cutoff.
    float sigma = max(e.radius, 1e-4) * 0.5;
    float gauss = e.strength * exp(-d2 / (2.0 * sigma * sigma));

    if (e.shape_type == 0u) {
        vec2 dir_vec = e.end_point - e.start_point;
        if (dot(dir_vec, dir_vec) > 1e-10) {
            // Radial-Directed — constant direction, Gaussian magnitude envelope.
            return gauss * normalize(dir_vec);
        }
        // Radial-Explosive — outward from center.
        float len = length(d_raw);
        if (len < 1e-6) return vec2(0.0);
        return gauss * (d_raw / len);
    }
    // Line-Explosive — outward from closest segment point.
    float len = length(d_raw);
    if (len < 1e-6) return vec2(0.0);
    return gauss * (d_raw / len);
}

void main() {
    uvec2 c = gl_GlobalInvocationID.xy;
    if (c.x >= p.grid_size.x || c.y >= p.grid_size.y) return;

    uint i = idx(c);
    NodeState me      = r_in.data[i];
    RestState rs      = r_rest.data[i];
    vec2      rest    = rs.anchor;
    float     rest_w  = rs.weight;

    if (c.x == 0u || c.y == 0u || c.x == p.grid_size.x - 1u || c.y == p.grid_size.y - 1u) {
        NodeState anchor;
        anchor.position = rest;
        anchor.velocity = vec2(0.0);
        r_out.data[i] = anchor;
        imageStore(positions_tex, ivec2(c), vec4(rest, 0.0, 0.0));
        return;
    }

    // Phase 7.2 gentle "viscosity" — inside an effector's radius, dampen ×0.98 (subtle, not
    // a freeze). Light viscosity preserves enough momentum for the ripple to carry outward
    // through the surrounding mesh while still absorbing the shatter-band frequencies.
    float vel_damp_local  = p.vel_damp;
    for (uint e2 = 0u; e2 < p.effector_count; e2++) {
        WarpEffectorData ed2 = r_eff.data[e2];
        vec2 center2 = (ed2.shape_type == 1u)
            ? closest_on_segment(ed2.start_point, ed2.end_point, me.position)
            : ed2.start_point;
        vec2 d2v = me.position - center2;
        if (dot(d2v, d2v) <= ed2.radius * ed2.radius) {
            vel_damp_local = p.vel_damp * 0.98;
            break;
        }
    }

    // Phase 7: force accumulator is pixel-displacement-per-step (no dt scaling anywhere).
    // Phase 9 Task 1: capture neighbor velocities from the read buffer for Laplacian blending.
    vec2 force = vec2(0.0);
    float rest_len_x = p.grid_spacing.x * p.rest_length_scale;
    float rest_len_y = p.grid_spacing.y * p.rest_length_scale;
    vec2 v_left = vec2(0.0), v_right = vec2(0.0), v_up = vec2(0.0), v_down = vec2(0.0);

    if (c.x > 0u) {
        NodeState n = r_in.data[idx(uvec2(c.x - 1u, c.y))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_x, p.stiffness, p.damping);
        v_left = n.velocity;
    }
    if (c.x + 1u < p.grid_size.x) {
        NodeState n = r_in.data[idx(uvec2(c.x + 1u, c.y))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_x, p.stiffness, p.damping);
        v_right = n.velocity;
    }
    if (c.y > 0u) {
        NodeState n = r_in.data[idx(uvec2(c.x, c.y - 1u))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_y, p.stiffness, p.damping);
        v_up = n.velocity;
    }
    if (c.y + 1u < p.grid_size.y) {
        NodeState n = r_in.data[idx(uvec2(c.x, c.y + 1u))];
        force += spring_force(me.position, me.velocity, n.position, n.velocity,
                              rest_len_y, p.stiffness, p.damping);
        v_down = n.velocity;
    }

    force += ((rest - me.position) * p.rest_stiffness - me.velocity * p.rest_damping) * rest_w;

    // Phase 7: impulse and force branches collapse in displacement math — both add directly
    // to the per-step force accumulator; structural shield below bounds the result regardless.
    for (uint e = 0u; e < p.effector_count; e++) {
        WarpEffectorData ed = r_eff.data[e];
        force += effector_force(me.position, ed);
    }

    // Phase 7.2 mass-inertial integration — forces are accelerations (unit mass):
    //   1) acc          = sum of forces (spring + anchor + effectors)
    //   2) new_vel      = velocity + acc                 (inertia — Δv per step)
    //   3) displacement = new_vel (structural-shielded)  (position += velocity)
    //   4) new_pos      = pos + displacement
    //   5) new_vel      = displacement * vel_damp        (velocity re-sync, then damp after move)
    // This keeps pos/vel coherent AND gives the mesh momentum so it "slides into place"
    // instead of teleporting — the distinction that eliminates shards at high impulse.
    vec2 acc          = force;
    vec2 new_vel      = me.velocity + acc;
    // Phase 9 Task 1 — Laplacian velocity blend. Couples each node's inertia to its 4-neighbor
    // average, killing the spatial checkerboard mode that dominates pure-parallel mass-spring
    // steppers. Boundary neighbors contribute zero (they're pinned) which is handled naturally
    // by the vec2(0.0) initialization — averages toward zero at edges, no special case needed.
    vec2 avg_v = (v_left + v_right + v_up + v_down) * 0.25;
    new_vel    = mix(new_vel, avg_v, p.velocity_blend);
    vec2 displacement = clamp(new_vel, -p.grid_spacing * 0.4, p.grid_spacing * 0.4);
    vec2 new_pos      = me.position + displacement;
    new_vel           = displacement * vel_damp_local;

    // Phase 8 — Inelastic Anchor Tether. Any node past ±1.5 cells from its rest anchor is
    // snapped back AND has its per-axis velocity killed on the clamped axis — an "inelastic
    // wall collision". Without the velocity-kill the node would infinitely ratchet against
    // the tether each step (stale outward momentum + clamp = pinned, maxVel frozen).
    vec2 max_offset  = p.grid_spacing * 1.5;
    vec2 clamped_pos = clamp(new_pos, rs.anchor - max_offset, rs.anchor + max_offset);
    if (clamped_pos.x != new_pos.x) new_vel.x = 0.0;
    if (clamped_pos.y != new_pos.y) new_vel.y = 0.0;
    new_pos = clamped_pos;

    // Jitter guard — 0.01 px in absolute pixels; well below any perceivable motion.
    if (length(new_vel) < 1e-2) new_vel = vec2(0.0);

    NodeState result;
    result.position = new_pos;
    result.velocity = new_vel;
    r_out.data[i]   = result;

    imageStore(positions_tex, ivec2(c), vec4(new_pos, new_vel));
}
