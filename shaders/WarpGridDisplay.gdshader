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
	// Phase 6.2 — smoothstep-mapped glow: soft ramp to saturation, no harsh knee.
	float glow = smoothstep(0.0, 1.0, v_speed * glow_gain);

	if (display_mode == 1) {
		// Texture mode — pure sprite warp. line_color tints the sampled color.
		vec4 tex_col = texture(main_tex, UV);
		vec4 base    = tex_col * line_color;
		vec4 bright  = base * 1.8;
		bright.a     = base.a;
		COLOR        = mix(base, bright, glow);
	} else {
		// Grid mode — procedural UV lines via fract + smoothstep.
		// fract(UV * grid_dims) repeats 0..1 once per cell. df.* * 2.0 widens edges into
		// soft "tubes" / "glow-wires" instead of razor-thin lines.
		vec2 cells = vec2(grid_dims);
		vec2 f     = fract(UV * cells);
		vec2 df    = fwidth(UV * cells);
		float line_x = 1.0 - smoothstep(0.0, df.x * 2.0, min(f.x, 1.0 - f.x));
		float line_y = 1.0 - smoothstep(0.0, df.y * 2.0, min(f.y, 1.0 - f.y));
		float line_mask = clamp(line_x + line_y, 0.0, 1.0);

		vec4 bg      = vec4(line_color.rgb * 0.08, 0.85);      // dark semi-transparent backdrop
		vec4 line_c  = line_color;
		vec4 bright  = line_c * 1.8;
		bright.a     = line_c.a;
		vec4 lit     = mix(line_c, bright, glow);
		COLOR        = mix(bg, lit, line_mask);
	}
	// Phase 6.2 — hard cap on each RGB channel. Belt-and-suspenders against the brightness blowout.
	COLOR.rgb = min(COLOR.rgb, vec3(1.0));
}
