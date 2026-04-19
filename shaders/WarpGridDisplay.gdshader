shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_tex : filter_nearest;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
uniform vec4 line_color : source_color = vec4(0.15, 0.55, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 200.0) = 20.0;

void vertex() {
	int w = grid_dims.x;
	int id = int(VERTEX_ID);
	ivec2 c = ivec2(id - (id / w) * w, id / w);
	vec4  s = texelFetch(positions_tex, c, 0);
	vec2  p = s.rg;
	vec2  v = s.ba;
	VERTEX = p * grid_size_pixels;

	// Brightness boost proportional to velocity magnitude.
	// glow_gain controls how fast lines go from base color toward white.
	float speed = length(v);
	float glow  = clamp(speed * glow_gain, 0.0, 1.0);
	COLOR = mix(line_color, vec4(1.0, 1.0, 1.0, line_color.a), glow);
}
