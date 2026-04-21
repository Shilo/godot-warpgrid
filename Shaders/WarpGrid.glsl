#[compute]
#version 450

#ifndef WARPGRID_VELOCITY_PASS
#ifndef WARPGRID_POSITION_PASS
#define WARPGRID_VELOCITY_PASS
#endif
#endif

layout(local_size_x = 4, local_size_y = 4, local_size_z = 1) in;

struct Position {
    vec2 pos;
};

struct Velocity {
    vec2 vel;
};

struct Force {
    vec2 force;
};

struct Neighbours {
    ivec2 neighbours[12];
};

layout(set = 0, binding = 0, std430) restrict buffer PositionBuffer {
    Position data[];
} posBuffer;

layout(set = 0, binding = 1, std430) restrict buffer VelocityBuffer {
    Velocity data[];
} velBuffer;

layout(set = 0, binding = 2, std430) restrict buffer ExternalForcesBuffer {
    Force data[];
} externalForcesBuffer;

layout(set = 0, binding = 3, std430) restrict buffer NeighboursBuffer {
    Neighbours data[];
} neighboursBuffer;

layout(set = 0, binding = 4, std140) uniform MouseParams {
    vec2 position;
    float strength;
    float radius;
    uint click_state;
} mouse;

layout(set = 0, binding = 5, std140) uniform PropertiesBuffer {
    float mass;
    float damping;
    float springStiffness;
    float springLength;
    float anchorStiffness;
} propertiesBuffer;

layout(set = 0, binding = 6, std140) uniform DeltaTimeBuffer {
    float deltaTime;
} deltaTimeBuffer;

layout(set = 0, binding = 7, rgba32f) uniform image2D positionsImage;

layout(set = 0, binding = 8, std430) restrict readonly buffer AnchorBuffer {
    Position data[];
} anchorBuffer;

const int gX = 15;
const int gY = 7;
const int sX = gX * 4;
const int sY = gY * 4;

vec2 getForceForNeighbour(
    const int idx,
    const ivec2 nIdx,
    const float stiffness,
    const float springLength,
    const float dampingFactor
) {
    vec2 d = posBuffer.data[nIdx.x].pos - posBuffer.data[idx].pos;
    float dLength = length(d);
    float divisor = dLength + (dLength == 0.0 ? 1.0 : 0.0);
    vec2 dN = d / (divisor == 0.0 ? 1.0 : divisor);
    vec2 force = stiffness * (d - springLength * dN) + dampingFactor * (velBuffer.data[nIdx.x].vel - velBuffer.data[idx].vel);
    return force * float(nIdx.y);
}

vec2 getMouseForce(const int idx) {
    if (mouse.click_state == 0u) {
        return vec2(0.0);
    }

    vec2 dir = posBuffer.data[idx].pos - mouse.position;
    float dist = length(dir);
    if (dist > mouse.radius || dist < 0.0001) {
        return vec2(0.0);
    }

    return (dir / dist) * mouse.strength * (1.0 - (dist / mouse.radius));
}

#ifdef WARPGRID_VELOCITY_PASS
void main() {
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    int idx = id.x + id.y * sX;
    int maxIdx = sX * sY;

    if (id.x >= sX || id.y >= sY || idx >= maxIdx) {
        return;
    }

    float mass = propertiesBuffer.mass;
    float stiffness = propertiesBuffer.springStiffness;
    float springLength = propertiesBuffer.springLength;

    ivec2 northNeighbour = neighboursBuffer.data[idx].neighbours[0];
    ivec2 northEastNeighbour = neighboursBuffer.data[idx].neighbours[1];
    ivec2 eastNeighbour = neighboursBuffer.data[idx].neighbours[2];
    ivec2 southEastNeighbour = neighboursBuffer.data[idx].neighbours[3];
    ivec2 southNeighbour = neighboursBuffer.data[idx].neighbours[4];
    ivec2 southWestNeighbour = neighboursBuffer.data[idx].neighbours[5];
    ivec2 westNeighbour = neighboursBuffer.data[idx].neighbours[6];
    ivec2 northWestNeighbour = neighboursBuffer.data[idx].neighbours[7];

    ivec2 northBendNeighbour = neighboursBuffer.data[idx].neighbours[8];
    ivec2 eastBendNeighbour = neighboursBuffer.data[idx].neighbours[9];
    ivec2 southBendNeighbour = neighboursBuffer.data[idx].neighbours[10];
    ivec2 westBendNeighbour = neighboursBuffer.data[idx].neighbours[11];

    float notEdge = float(
        northBendNeighbour.y != 0 &&
        eastBendNeighbour.y != 0 &&
        westBendNeighbour.y != 0 &&
        southBendNeighbour.y != 0
    );

    vec2 northForce = getForceForNeighbour(idx, northNeighbour, stiffness, springLength, 0.0);
    vec2 northEastForce = getForceForNeighbour(idx, northEastNeighbour, stiffness, springLength, 0.0);
    vec2 eastForce = getForceForNeighbour(idx, eastNeighbour, stiffness, springLength, 0.0);
    vec2 southEastForce = getForceForNeighbour(idx, southEastNeighbour, stiffness, springLength, 0.0);
    vec2 southForce = getForceForNeighbour(idx, southNeighbour, stiffness, springLength, 0.0);
    vec2 southWestForce = getForceForNeighbour(idx, southWestNeighbour, stiffness, springLength, 0.0);
    vec2 westForce = getForceForNeighbour(idx, westNeighbour, stiffness, springLength, 0.0);
    vec2 northWestForce = getForceForNeighbour(idx, northWestNeighbour, stiffness, springLength, 0.0);

    vec2 northBendForce = getForceForNeighbour(idx, northBendNeighbour, stiffness, springLength, 0.0);
    vec2 eastBendForce = getForceForNeighbour(idx, eastBendNeighbour, stiffness, springLength, 0.0);
    vec2 westBendForce = getForceForNeighbour(idx, southBendNeighbour, stiffness, springLength, 0.0);
    vec2 southBendForce = getForceForNeighbour(idx, westBendNeighbour, stiffness, springLength, 0.0);

    vec2 internalForce = (
        northForce + eastForce + westForce + southForce +
        northEastForce + northWestForce + southEastForce + southWestForce +
        northBendForce + eastBendForce + westBendForce + southBendForce
    );

    vec2 anchorDir = anchorBuffer.data[idx].pos - posBuffer.data[idx].pos;
    vec2 anchorForce = anchorDir * propertiesBuffer.anchorStiffness;
    vec2 mouseForce = getMouseForce(idx);
    vec2 force = internalForce + anchorForce + externalForcesBuffer.data[idx].force + mouseForce;
    vec2 acceleration = force / (mass == 0.0 ? 1.0 : mass);
    float delta = deltaTimeBuffer.deltaTime;
    vec2 vDelta = notEdge * acceleration * delta;
    vec2 newVel = velBuffer.data[idx].vel + vDelta;
    velBuffer.data[idx].vel = newVel;
}
#elif defined(WARPGRID_POSITION_PASS)
void main() {
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    int idx = id.x + id.y * sX;

    if (id.x >= sX || id.y >= sY) {
        return;
    }

    float delta = deltaTimeBuffer.deltaTime;
    vec2 vel = velBuffer.data[idx].vel * propertiesBuffer.damping;
    externalForcesBuffer.data[idx].force = vec2(0.0);
    velBuffer.data[idx].vel = vel;
    posBuffer.data[idx].pos = posBuffer.data[idx].pos + (vel * delta);
    imageStore(positionsImage, id, vec4(posBuffer.data[idx].pos, 0.0, 1.0));
}
#endif
