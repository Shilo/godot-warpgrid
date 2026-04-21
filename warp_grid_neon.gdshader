shader_type canvas_item;
render_mode unshaded, blend_add;

uniform vec4 tint : source_color = vec4(0.34, 0.72, 1.0, 1.0);
uniform float glow_strength : hint_range(0.0, 8.0) = 2.8;
uniform float core_strength : hint_range(0.0, 4.0) = 1.25;

void fragment()
{
    float edge_distance = abs(UV.y * 2.0 - 1.0);
    float glow = pow(max(0.0, 1.0 - edge_distance), 1.6);
    float core = pow(max(0.0, 1.0 - edge_distance), 6.0);
    vec4 base = COLOR * tint;

    COLOR = vec4(base.rgb * (glow * glow_strength + core * core_strength), base.a * glow);
}
