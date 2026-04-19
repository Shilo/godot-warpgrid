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
    float _pad0;             // offset 52 (std140 padding to align vec2 at 56)
    vec2  grid_aspect;       // offset 56 — (pixel_w, pixel_h) / min(pixel_w, pixel_h)
} p;

layout(set = 0, binding = 5, rg32f) uniform restrict writeonly image2D positions_tex;

uint idx(uvec2 c) { return c.y * p.grid_size.x + c.x; }

vec2 spring_force(vec2 me_pos, vec2 me_vel, vec2 other_pos, vec2 other_vel,
                  float rest_len, float k, float c) {
    vec2  delta = other_pos - me_pos;
    float len   = length(delta);
    if (len < 1e-7) return vec2(0.0);
    vec2  dir   = delta / len;
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

void main() {
    uvec2 c = gl_GlobalInvocationID.xy;
    if (c.x >= p.grid_size.x || c.y >= p.grid_size.y) return;

    uint i = idx(c);
    NodeState me   = r_in.data[i];
    vec2      rest = r_rest.data[i].anchor;

    if (c.x == 0u || c.y == 0u || c.x == p.grid_size.x - 1u || c.y == p.grid_size.y - 1u) {
        NodeState anchor;
        anchor.position = rest;
        anchor.velocity = vec2(0.0);
        r_out.data[i] = anchor;
        imageStore(positions_tex, ivec2(c), vec4(rest, 0.0, 0.0));
        return;
    }

    vec2 force = vec2(0.0);
    float rest_len = p.grid_spacing.x * p.rest_length_scale;

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

    force += (rest - me.position) * p.rest_stiffness - me.velocity * p.rest_damping;

    vec2 impulse_v = vec2(0.0);
    for (uint e = 0u; e < p.effector_count; e++) {
        WarpEffectorData ed = r_eff.data[e];
        vec2 ef = effector_force(me.position, ed);
        if (ed.behavior_type == 1u) {
            float mag = length(ef);
            if (mag > p.impulse_cap / max(p.dt, 1e-6)) {
                ef *= (p.impulse_cap / max(p.dt, 1e-6)) / max(mag, 1e-7);
            }
            impulse_v += ef * p.dt;
        } else {
            force += ef;
        }
    }

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
