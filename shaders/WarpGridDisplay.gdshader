shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_tex : filter_linear;
uniform sampler2D main_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
uniform vec2 phys_tex_size = vec2(37.0, 21.0);
uniform vec4 line_color : source_color = vec4(0.1, 1.0, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 5.0) = 0.08;
uniform int display_mode = 0;

varying float v_energy;

void vertex() {
	vec2 sample_uv = (UV * (phys_tex_size - vec2(1.0)) + vec2(0.5)) / phys_tex_size;
	vec4 s = texture(positions_tex, sample_uv);
	vec2 current_pos = s.xy;
	vec2 prev_pos = s.zw;
	VERTEX = current_pos;
	v_energy = length(current_pos - prev_pos);
}

void fragment() {
	float glow = clamp(pow(v_energy * glow_gain, 2.0), 0.0, 1.0);

	if (display_mode == 1) {
		vec4 tex_col = texture(main_tex, UV);
		vec4 base = tex_col * line_color;
		vec4 bright = base * 2.0;
		bright.a = base.a;
		COLOR = mix(base, bright, glow);
	} else {
		vec2 cells = vec2(grid_dims);
		vec2 f = fract(UV * cells);
		vec2 df = fwidth(UV * cells);
		float edge = min(f.x, 1.0 - f.x);
		float edge_y = min(f.y, 1.0 - f.y);

		float taper = 1.0 - clamp(v_energy * 0.06, 0.0, 0.75);
		float core_x = 1.0 - smoothstep(0.0, df.x * 2.0 * taper, edge);
		float core_y = 1.0 - smoothstep(0.0, df.y * 2.0 * taper, edge_y);
		float core = clamp(core_x + core_y, 0.0, 1.0);

		float halo_x = 1.0 - smoothstep(0.0, df.x * 9.0 * taper, edge);
		float halo_y = 1.0 - smoothstep(0.0, df.y * 9.0 * taper, edge_y);
		float halo = clamp((halo_x + halo_y) * 0.25, 0.0, 1.0);

		float line_mask = max(core, halo);
		vec4 bg = vec4(line_color.rgb * 0.08, 0.85);
		vec4 bright = line_color * 2.0;
		bright.a = line_color.a;
		vec4 lit = mix(line_color, bright, glow);
		COLOR = mix(bg, lit, line_mask);
	}

	COLOR.rgb = min(COLOR.rgb, vec3(1.0));
}
