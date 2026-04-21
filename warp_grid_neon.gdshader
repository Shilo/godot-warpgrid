shader_type canvas_item;
render_mode unshaded;

uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float glow_strength : hint_range(0.0, 3.0) = 0.35;
uniform float core_strength : hint_range(0.0, 2.0) = 0.2;

void fragment()
{
    float center = 1.0 - abs(UV.y * 2.0 - 1.0);
    float glow = smoothstep(0.0, 1.0, center);
    float core = pow(glow, 4.0);
    vec4 base = COLOR * tint;
    float brightness = 1.0 + glow * glow_strength + core * core_strength;
    float alpha = base.a * mix(0.9, 1.0, glow);

    COLOR = vec4(base.rgb * brightness, alpha);
}
