shader_type canvas_item;
render_mode unshaded;

// Phase 6.7: filter_linear — GPU bilinear-samples the low-res physics texture so the
// high-res visual mesh reads interpolated displacement, not the raw texel grid.
uniform sampler2D positions_tex : filter_linear;
uniform sampler2D main_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
// Phase 6.7: physics texture dimensions (node count, e.g. 37×21 for a 36×20 phys grid).
// Used to offset UV sampling to texel centers for exact endpoint alignment.
uniform vec2 phys_tex_size = vec2(37.0, 21.0);
// Phase 11 Task 4 — neon cyan. Bright, saturated, arcade-loud.
uniform vec4 line_color : source_color = vec4(0.1, 1.0, 1.0, 1.0);
// Phase 13 — glow_gain now controls how strongly peaks above the highlight threshold bloom.
uniform float glow_gain : hint_range(0.0, 5.0) = 0.1;

// Phase 6.1: mutually exclusive render modes.
// 0 = Grid (procedural UV lines); 1 = Texture (sample main_tex).
uniform int display_mode = 0;

varying float v_speed;

void vertex() {
	// Phase 13 — scalar wave warp. Texture channels: .r = h_curr (scalar height, pixels),
	// .ba = latched push direction (unit vector). VERTEX enters as the anchor position
	// (MeshHelper.BuildGridVertices lays vertices at the anchor grid), so the final
	// displacement is just anchor + dir * h.
	vec2 sample_uv = (UV * (phys_tex_size - vec2(1.0)) + vec2(0.5)) / phys_tex_size;
	vec4 s = texture(positions_tex, sample_uv);
	float h   = s.r;
	vec2  dir = s.ba;
	VERTEX += dir * h;
	// Glow is driven by instantaneous height magnitude now — speed no longer exists.
	v_speed = abs(h);
}

void fragment() {
	// Suppress the background glow until a ripple reaches a visible crest.
	float glow = smoothstep(0.0, 1.0, max(v_speed - 2.0, 0.0) * glow_gain);

	if (display_mode == 1) {
		// Texture mode — pure sprite warp. line_color tints the sampled color.
		vec4 tex_col = texture(main_tex, UV);
		vec4 base    = tex_col * line_color;
		vec4 bright  = base * 1.8;
		bright.a     = base.a;
		COLOR        = mix(base, bright, glow);
	} else {
		// Grid mode — procedural UV lines via fract + smoothstep.
		// Phase 6.3: two-pass — core (df*3.0) gives a thick "tube" line;
		// halo (df*9.0) bleeds a soft bloom around the core to mask visible triangle edges
		// at low cell counts (e.g. 36x20). Max-composited so halo only brightens where core is dim.
		vec2 cells = vec2(grid_dims);
		vec2 f     = fract(UV * cells);
		vec2 df    = fwidth(UV * cells);
		float edge = min(f.x, 1.0 - f.x);
		float edge_y = min(f.y, 1.0 - f.y);

		// Thin the line specifically at the wave crest so the peak reads like a taut water skin.
		float peak = smoothstep(2.0, 8.0, v_speed);
		float taper = mix(1.0, 0.35, peak);

		float core_x = 1.0 - smoothstep(0.0, df.x * 2.0 * taper, edge);
		float core_y = 1.0 - smoothstep(0.0, df.y * 2.0 * taper, edge_y);
		float core   = clamp(core_x + core_y, 0.0, 1.0);

		// Wide, dim halo (df*9 @ 0.25 alpha) bleeds across triangle boundaries so the mesh
		// reads as continuous glow-wire at rest, not a visible quad tessellation.
		float halo_x = 1.0 - smoothstep(0.0, df.x * 9.0 * taper, edge);
		float halo_y = 1.0 - smoothstep(0.0, df.y * 9.0 * taper, edge_y);
		float halo   = clamp((halo_x + halo_y) * 0.25, 0.0, 1.0);

		float line_mask = max(core, halo);

		vec4 bg      = vec4(line_color.rgb * 0.08, 0.85);      // dark semi-transparent backdrop
		vec4 bright  = line_color * 1.8;
		bright.a     = line_color.a;
		vec4 lit     = mix(line_color, bright, glow);
		COLOR        = mix(bg, lit, line_mask);
	}
	// Phase 6.2 — hard cap on each RGB channel. Belt-and-suspenders against the brightness blowout.
	COLOR.rgb = min(COLOR.rgb, vec3(1.0));
}
