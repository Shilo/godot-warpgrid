shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_tex : filter_nearest;
uniform sampler2D main_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
uniform vec4 line_color : source_color = vec4(0.15, 0.55, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 200.0) = 8.0;

// Phase 6.1: mutually exclusive render modes.
// 0 = Grid (procedural UV lines); 1 = Texture (sample main_tex).
uniform int display_mode = 0;

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
	// Glow capped at 0.6 to preserve hue — prevents full-white blowout at peak velocity.
	float glow = clamp(v_speed * glow_gain, 0.0, 0.6);

	if (display_mode == 1) {
		// Texture mode — pure sprite warp. line_color tints the sampled color.
		vec4 tex_col = texture(main_tex, UV);
		vec4 base    = tex_col * line_color;
		vec4 bright  = clamp(base * 1.8, vec4(0.0), vec4(1.0, 1.0, 1.0, base.a));
		bright.a     = base.a;
		COLOR        = mix(base, bright, glow);
	} else {
		// Grid mode — procedural UV lines via fract + smoothstep.
		// fract(UV * grid_dims) repeats 0..1 once per cell; we detect proximity to 0 or 1
		// edges as the line. Width scales with derivative so lines stay ~constant pixel width.
		vec2 cells = vec2(grid_dims);
		vec2 f     = fract(UV * cells);
		vec2 df    = fwidth(UV * cells);
		float line_x = 1.0 - smoothstep(0.0, df.x * 1.5, min(f.x, 1.0 - f.x));
		float line_y = 1.0 - smoothstep(0.0, df.y * 1.5, min(f.y, 1.0 - f.y));
		float line_mask = clamp(line_x + line_y, 0.0, 1.0);

		vec4 bg      = vec4(line_color.rgb * 0.08, 0.85);      // dark semi-transparent backdrop
		vec4 line_c  = line_color;
		vec4 bright  = clamp(line_c * 1.8, vec4(0.0), vec4(1.0, 1.0, 1.0, line_c.a));
		bright.a     = line_c.a;
		vec4 lit     = mix(line_c, bright, glow);
		COLOR        = mix(bg, lit, line_mask);
	}
}
