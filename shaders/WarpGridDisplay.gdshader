shader_type canvas_item;
render_mode unshaded, blend_mix;

uniform sampler2D positions_tex : filter_nearest, repeat_disable;
uniform vec2  grid_size_pixels = vec2(1600.0, 900.0);
uniform ivec2 grid_dims        = ivec2(100, 100);
uniform vec4  line_color : source_color = vec4(0.15, 0.55, 1.0, 1.0);

void vertex() {
	int w = grid_dims.x;
	int id = int(VERTEX_ID);
	ivec2 c = ivec2(id - (id / w) * w, id / w);
	vec2 p = texelFetch(positions_tex, c, 0).rg;
	VERTEX = p * grid_size_pixels;
}

void fragment() {
	COLOR = line_color;
}
