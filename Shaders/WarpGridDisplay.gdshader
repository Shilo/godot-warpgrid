shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_texture : filter_nearest, repeat_disable;
uniform sampler2D anchor_texture : filter_nearest, repeat_disable;
uniform float physics_min_spacing = 1.0;
uniform vec4 line_color : source_color = vec4(0.14, 0.81, 0.96, 1.0);

varying float v_energy;

void vertex() {
    VERTEX = textureLod(positions_texture, UV, 0.0).rg;
    vec2 anchor_pos = textureLod(anchor_texture, UV, 0.0).rg;
    v_energy = length(VERTEX - anchor_pos) / physics_min_spacing;
}

void fragment() {
    float energy = clamp(v_energy, 0.0, 1.0);
    COLOR = vec4(line_color.rgb * (0.55 + energy * 0.45), line_color.a);
}
