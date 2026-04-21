shader_type canvas_item;
render_mode unshaded;

// positions_tex is CPU-packed RGBA32F:
// .xy = current position in pixels
// .zw = anchor position in pixels
// The sampler stays filter_linear so the display mesh bilinear-samples the low-res
// physics texture instead of exposing the raw texel grid.
uniform sampler2D positions_tex : filter_linear;
uniform sampler2D velocity_tex : filter_linear;
uniform sampler2D main_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec2 grid_size_pixels;
uniform ivec2 display_grid_dims;
// Physics texture dimensions in texels (node count, e.g. 37x21 for a 36x20 phys grid).
// Used to offset UV sampling to texel centers for exact endpoint alignment.
uniform vec2 phys_tex_size = vec2(37.0, 21.0);
uniform float physics_min_spacing = 32.0;
uniform vec4 line_color : source_color = vec4(0.1, 1.0, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 5.0) = 1.0;
uniform float phosphor_persistence : hint_range(0.0, 2.0) = 0.7;
uniform float hot_shift_strength : hint_range(0.0, 1.0) = 0.45;

// 0 = Grid (procedural UV lines); 1 = Texture (sample main_tex).
uniform int display_mode = 0;

varying float v_energy;
varying vec2 v_warp;

vec4 sample_positions(vec2 uv) {
	vec2 sample_uv = (uv * (phys_tex_size - vec2(1.0)) + vec2(0.5)) / phys_tex_size;
	return texture(positions_tex, sample_uv);
}

float sample_velocity_energy(vec2 uv) {
	vec2 sample_uv = (uv * (phys_tex_size - vec2(1.0)) + vec2(0.5)) / phys_tex_size;
	return texture(velocity_tex, sample_uv).r / max(physics_min_spacing, 1.0);
}

float normalized_energy(vec2 warp) {
	return length(warp) / max(physics_min_spacing, 1.0);
}

vec3 hot_wire_tint(vec3 base, float velocity_energy) {
	float hot = smoothstep(0.45, 1.25, velocity_energy) * hot_shift_strength;
	vec3 hot_color = mix(vec3(1.0, 0.82, 0.55), vec3(1.0), hot * 0.7);
	return mix(base, hot_color, hot);
}

void vertex() {
	// Mesh vertices are laid out on the rest grid, so the rendered warp is the delta
	// between the current sampled position and its anchor.
	vec4 packed = sample_positions(UV);
	vec2 position = packed.xy;
	vec2 anchor = packed.zw;
	v_warp = position - anchor;
	v_energy = normalized_energy(v_warp);
	VERTEX += v_warp;
}

vec4 apply_chromatic_aberration(sampler2D tex, vec2 uv, vec2 direction, float energy, float tint_scale) {
	float overload = smoothstep(0.95, 1.8, energy);
	float amount = clamp(pow(max(energy, 0.0), 1.75) * tint_scale, 0.0, 1.0) * (0.00125 + (0.0028 * overload));
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
	vec4 packed = sample_positions(UV);
	vec2 frag_warp = packed.xy - packed.zw;
	float energy = max(v_energy, normalized_energy(frag_warp));
	float velocity_energy = sample_velocity_energy(UV);
	float glow = smoothstep(0.0, 1.0, energy * glow_gain);
	float persistence = smoothstep(0.12, 1.15, energy) * phosphor_persistence;
	vec2 aberr_dir = (length(frag_warp) > 1e-5) ? normalize(frag_warp) : vec2(1.0, 0.0);
	float overload = smoothstep(0.95, 1.85, energy);
	float aberr = clamp((pow(max(energy, 0.0), 1.8) * 0.075) + (overload * 0.22), 0.0, 1.0);
	vec3 hot_rgb = hot_wire_tint(line_color.rgb, max(velocity_energy, energy * 0.7));

	if (display_mode == 1) {
		// Texture mode — sample the sprite, tint it lightly, and add subtle energy glow.
		vec4 tex_col = apply_chromatic_aberration(main_tex, UV, aberr_dir, energy, 1.0);
		vec4 base = tex_col * vec4(hot_rgb, line_color.a);
		vec4 bright = base * (1.45 + (0.35 * persistence));
		bright.a = base.a;
		COLOR = mix(base, bright, glow);
		COLOR.rgb += hot_rgb * persistence * 0.18;
	} else {
		vec2 cells = vec2(display_grid_dims);
		float taper = mix(0.92, 0.34, smoothstep(0.0, 0.95, energy));
		float line_mask_r = grid_line_mask(UV + aberr_dir * aberr * (0.55 + (0.9 * overload)), cells, taper);
		float line_mask_g = grid_line_mask(UV, cells, taper);
		float line_mask_b = grid_line_mask(UV - aberr_dir * aberr * (0.55 + (0.9 * overload)), cells, taper);
		float persistence_mask = grid_line_mask(UV, cells, taper * 2.1);
		vec3 lit_rgb = hot_rgb * vec3(line_mask_r, line_mask_g, line_mask_b);
		float line_mask = max(max(line_mask_r, line_mask_g), line_mask_b);
		line_mask = max(line_mask, persistence_mask * 0.24 * persistence);

		vec4 bg = vec4(line_color.rgb * 0.08, 0.85);
		vec3 persistence_rgb = hot_rgb * persistence * persistence_mask * 0.42;
		vec4 bright = vec4((lit_rgb * (1.45 + (0.5 * persistence))) + persistence_rgb, line_color.a);
		vec4 lit = mix(vec4(lit_rgb, line_color.a), bright, glow);
		COLOR = mix(bg, lit, line_mask);
		COLOR.rgb += persistence_rgb * 0.6;
	}
	COLOR.rgb = min(COLOR.rgb, vec3(1.0));
}
