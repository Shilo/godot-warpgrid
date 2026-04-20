#[compute]
#version 450
// GPU_MANIFEST STATE_STRIDE=16 REST_STRIDE=16 EFF_STRIDE=32 PARAM_SIZE=64 SPIRAL_FACTOR_OFFSET=52

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

struct NodeState       { vec2 current; vec2 prev; };
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
layout(set = 0, binding = 2, std430) restrict readonly buffer RestBuf { RestState data[]; } r_rest;
layout(set = 0, binding = 3, std430) restrict readonly buffer EffBuf  { WarpEffectorData data[]; } r_eff;

layout(set = 0, binding = 4, std140) uniform GridParams {
    uvec2 grid_size;            // 0
    vec2  grid_spacing;         // 8 (pixels)
    float neighbor_stiffness;   // 16
    float anchor_stiffness;     // 20
    float global_damping;       // 24
    float friction;             // 28
    uint  effector_count;       // 32
    float force_scaler;         // 36
    uint  phase_kind;           // 40
    uint  apply_effectors;      // 44
    uint  boundary_pinning;     // 48
    float spiral_factor;        // 52
} p;

layout(set = 0, binding = 5, rgba32f) uniform restrict writeonly image2D positions_tex;

const uint PHASE_PREDICTION = 0u;
const uint PHASE_SOLVER     = 1u;
const uint PHASE_FINALIZE   = 2u;

uint idx(uvec2 c) { return c.y * p.grid_size.x + c.x; }

bool is_edge(uvec2 c) {
    return c.x == 0u || c.y == 0u || c.x == p.grid_size.x - 1u || c.y == p.grid_size.y - 1u;
}

vec2 closest_on_segment(vec2 a, vec2 b, vec2 p0) {
    vec2  ba = b - a;
    float denom = dot(ba, ba);
    if (denom < 1e-10) return a;
    float t = clamp(dot(p0 - a, ba) / denom, 0.0, 1.0);
    return a + t * ba;
}

vec2 effector_offset(vec2 sample_pos) {
    vec2 offset = vec2(0.0);

    for (uint e = 0u; e < p.effector_count; e++) {
        WarpEffectorData ed = r_eff.data[e];
        vec2 center = (ed.shape_type == 1u)
            ? closest_on_segment(ed.start_point, ed.end_point, sample_pos)
            : ed.start_point;

        vec2 d_raw = sample_pos - center;
        float d2 = dot(d_raw, d_raw);
        if (d2 > ed.radius * ed.radius) continue;

        float len = length(d_raw);
        vec2 radial_dir = (len > 1e-6) ? (d_raw / len) : vec2(0.0);
        vec2 inward_dir = -radial_dir;
        vec2 push_dir = radial_dir;

        float sigma = max(ed.radius, 1e-4) * 0.5;
        float amp = ed.strength * exp(-d2 / (2.0 * sigma * sigma)) * p.force_scaler;

        if (ed.behavior_type == 2u) {
            vec2 tangent = vec2(-radial_dir.y, radial_dir.x);
            vec2 spiral_dir = mix(inward_dir, tangent, clamp(p.spiral_factor, 0.0, 1.0));
            float spiral_len = length(spiral_dir);
            push_dir = (spiral_len > 1e-6) ? (spiral_dir / spiral_len) : tangent;
        } else if (ed.behavior_type == 3u) {
            float damped_inv_sq = (ed.radius * ed.radius) / max(d2 + sigma * sigma, 1e-4);
            offset += inward_dir * (ed.strength * p.force_scaler * damped_inv_sq * 0.18);
            continue;
        } else if (ed.shape_type == 0u) {
            vec2 dv = ed.end_point - ed.start_point;
            if (dot(dv, dv) > 1e-10)
                push_dir = normalize(dv);
        }

        if (ed.behavior_type == 1u)
            amp *= 1.5;
        else if (ed.behavior_type == 2u)
            amp *= 1.15;

        offset += push_dir * amp;
    }

    return offset;
}

vec2 solve_neighbors(uvec2 c, vec2 position) {
    vec2 correction = vec2(0.0);
    float samples = 0.0;

    if (c.x > 0u) {
        vec2 other = r_in.data[idx(uvec2(c.x - 1u, c.y))].current;
        vec2 delta = position - other;
        float len = length(delta);
        if (len > 1e-6) {
            vec2 target = other + delta / len * p.grid_spacing.x;
            correction += target - position;
            samples += 1.0;
        }
    }
    if (c.x + 1u < p.grid_size.x) {
        vec2 other = r_in.data[idx(uvec2(c.x + 1u, c.y))].current;
        vec2 delta = position - other;
        float len = length(delta);
        if (len > 1e-6) {
            vec2 target = other + delta / len * p.grid_spacing.x;
            correction += target - position;
            samples += 1.0;
        }
    }
    if (c.y > 0u) {
        vec2 other = r_in.data[idx(uvec2(c.x, c.y - 1u))].current;
        vec2 delta = position - other;
        float len = length(delta);
        if (len > 1e-6) {
            vec2 target = other + delta / len * p.grid_spacing.y;
            correction += target - position;
            samples += 1.0;
        }
    }
    if (c.y + 1u < p.grid_size.y) {
        vec2 other = r_in.data[idx(uvec2(c.x, c.y + 1u))].current;
        vec2 delta = position - other;
        float len = length(delta);
        if (len > 1e-6) {
            vec2 target = other + delta / len * p.grid_spacing.y;
            correction += target - position;
            samples += 1.0;
        }
    }

    if (samples <= 0.0) return position;
    return position + (correction / samples) * clamp(p.neighbor_stiffness, 0.0, 1.0);
}

vec2 solve_anchor(vec2 position, RestState rs) {
    float stiffness = clamp(p.anchor_stiffness * rs.weight, 0.0, 1.0);
    return mix(position, rs.anchor, stiffness);
}

void main() {
    uvec2 c = gl_GlobalInvocationID.xy;
    if (c.x >= p.grid_size.x || c.y >= p.grid_size.y) return;

    uint i = idx(c);
    NodeState me = r_in.data[i];
    RestState rs = r_rest.data[i];
    bool edge = is_edge(c);

    NodeState out_state = me;

    if (p.phase_kind == PHASE_PREDICTION) {
        vec2 velocity = (me.current - me.prev) * clamp(p.global_damping, 0.0, 1.0);
        vec2 predicted = me.current + velocity;
        if (p.apply_effectors != 0u)
            predicted += effector_offset(predicted);

        out_state.current = predicted;
        out_state.prev = me.current;
    } else if (p.phase_kind == PHASE_SOLVER) {
        vec2 projected = me.current;
        projected = solve_anchor(projected, rs);
        projected = solve_neighbors(c, projected);
        projected = solve_anchor(projected, rs);

        out_state.current = projected;
        out_state.prev = me.prev;
    } else {
        vec2 finalized = solve_anchor(me.current, rs);
        vec2 damped_prev = mix(finalized, me.prev, 1.0 - clamp(p.friction, 0.0, 1.0));
        out_state.current = finalized;
        out_state.prev = damped_prev;
    }

    if (p.boundary_pinning != 0u && edge) {
        out_state.current = rs.anchor;
        out_state.prev = rs.anchor;
    }

    r_out.data[i] = out_state;
    imageStore(positions_tex, ivec2(c), vec4(out_state.current, out_state.prev));
}
