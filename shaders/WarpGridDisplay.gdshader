shader_type canvas_item;
render_mode unshaded;

// positions_tex is CPU-packed RGBA32F:
// .xy = current position in pixels
// .zw = anchor position in pixels
// The sampler stays filter_linear so the display mesh bilinear-samples the low-res
// physics texture instead of exposing the raw texel grid.
uniform sampler2D positions_tex : filter_linear;
uniform sampler2D main_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
// Physics texture dimensions in texels (node count, e.g. 37x21 for a 36x20 phys grid).
// Used to offset UV sampling to texel centers for exact endpoint alignment.
uniform vec2 phys_tex_size = vec2(37.0, 21.0);
uniform vec4 line_color : source_color = vec4(0.1, 1.0, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 5.0) = 0.1;

// 0 = Grid (procedural UV lines); 1 = Texture (sample main_tex).
uniform int display_mode = 0;

varying float v_energy;
varying vec2 v_warp;

void vertex() {
	// Mesh vertices are laid out on the rest grid, so the rendered warp is the delta
	// between the current sampled position and its anchor.
	vec2 sample_uv = (UV * (phys_tex_size - vec2(1.0)) + vec2(0.5)) / phys_tex_size;
	vec4 packed = texture(positions_tex, sample_uv);
	vec2 position = packed.xy;
	vec2 anchor = packed.zw;
	v_warp = position - anchor;
	v_energy = length(v_warp);
	VERTEX += v_warp;
}

vec4 apply_chromatic_aberration(sampler2D tex, vec2 uv, vec2 direction, float energy, float tint_scale) {
	float amount = clamp(energy * tint_scale, 0.0, 1.0) * 0.0015;
	vec2 offset = direction * amount;
	float r = texture(tex, uv + offset).r;
	float g = texture(tex, uv).g;
	float b = texture(tex, uv - offset).b;
	return vec4(r, g, b, texture(tex, uv).a);
}

float grid_line_mask(vec2 uv, vec2 cells, float taper) {
	vec2 grid_uv = uv * cells;
	vec2 f = fract(grid_uv);
	vec2 df = fwidth(grid_uv);
	float edge = min(f.x, 1.0 - f.x);
	float edge_y = min(f.y, 1.0 - f.y);
	float core_x = 1.0 - smoothstep(0.0, df.x * 2.0 * taper, edge);
	float core_y = 1.0 - smoothstep(0.0, df.y * 2.0 * taper, edge_y);
	float core = clamp(core_x + core_y, 0.0, 1.0);
	float halo_x = 1.0 - smoothstep(0.0, df.x * 9.0 * taper, edge);
	float halo_y = 1.0 - smoothstep(0.0, df.y * 9.0 * taper, edge_y);
	float halo = clamp((halo_x + halo_y) * 0.25, 0.0, 1.0);
	return max(core, halo);
}

void fragment() {
	float glow = smoothstep(0.0, 1.0, v_energy * glow_gain);
	vec2 aberr_dir = (length(v_warp) > 1e-5) ? normalize(v_warp) : vec2(1.0, 0.0);
	float aberr = clamp(v_energy * 0.0025, 0.0, 1.0);

	if (display_mode == 1) {
		// Texture mode — sample the sprite, tint it lightly, and add subtle energy glow.
		vec4 tex_col = apply_chromatic_aberration(main_tex, UV, aberr_dir, v_energy, 1.0);
		vec4 base = tex_col * line_color;
		vec4 bright = base * 1.55;
		bright.a = base.a;
		COLOR = mix(base, bright, glow);
	} else {
		vec2 cells = vec2(grid_dims);
		float taper = mix(1.0, 0.55, smoothstep(0.0, 24.0, v_energy));
		float line_mask_r = grid_line_mask(UV + aberr_dir * aberr * 0.6, cells, taper);
		float line_mask_g = grid_line_mask(UV, cells, taper);
		float line_mask_b = grid_line_mask(UV - aberr_dir * aberr * 0.6, cells, taper);
		vec3 lit_rgb = line_color.rgb * vec3(line_mask_r, line_mask_g, line_mask_b);
		float line_mask = max(max(line_mask_r, line_mask_g), line_mask_b);

		vec4 bg = vec4(line_color.rgb * 0.08, 0.85);
		vec4 bright = vec4(lit_rgb * 1.55, line_color.a);
		vec4 lit = mix(vec4(lit_rgb, line_color.a), bright, glow);
		COLOR = mix(bg, lit, line_mask);
	}
	COLOR.rgb = min(COLOR.rgb, vec3(1.0));
}
