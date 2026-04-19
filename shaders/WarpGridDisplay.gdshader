shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_tex : filter_nearest;
uniform sampler2D main_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
uniform vec4 line_color : source_color = vec4(0.15, 0.55, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 200.0) = 8.0;

varying float v_speed;

void vertex() {
	int w = grid_dims.x;
	int id = int(VERTEX_ID);
	ivec2 c = ivec2(id - (id / w) * w, id / w);
	vec4  s = texelFetch(positions_tex, c, 0);
	vec2  p = s.rg;
	vec2  v = s.ba;
	VERTEX = p * grid_size_pixels;
	v_speed = length(v);
}

void fragment() {
	vec4 tex_col = texture(main_tex, UV);
	vec4 base = tex_col * line_color;
	float glow = clamp(v_speed * glow_gain, 0.0, 1.0);
	COLOR = mix(base, vec4(1.0, 1.0, 1.0, base.a), glow);
}
