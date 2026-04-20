shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_tex : filter_linear;
uniform sampler2D main_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D energy_gradient_tex : source_color, filter_linear, repeat_disable;
uniform vec2 grid_size_pixels;
uniform ivec2 grid_dims;
uniform vec2 phys_tex_size = vec2(37.0, 21.0);
uniform vec4 line_color : source_color = vec4(0.1, 1.0, 1.0, 1.0);
uniform float glow_gain : hint_range(0.0, 5.0) = 0.08;
uniform float chromatic_strength : hint_range(0.0, 8.0) = 1.0;
uniform float persistence : hint_range(0.0, 1.0) = 0.1;
uniform float motion_blur_strength : hint_range(0.0, 1.0) = 0.2;
uniform float stretch_taper_strength : hint_range(0.0, 2.0) = 1.0;
uniform int display_mode = 0;

varying float v_energy;
varying float v_stretch;
varying vec2 v_velocity;
const float CHROMATIC_THRESHOLD = 1.2;

vec2 sample_position(vec2 sample_uv) {
	return texture(positions_tex, clamp(sample_uv, vec2(0.0), vec2(1.0))).xy;
}

void vertex() {
	vec2 sample_uv = (UV * (phys_tex_size - vec2(1.0)) + vec2(0.5)) / phys_tex_size;
	vec4 s = texture(positions_tex, sample_uv);
	vec2 current_pos = s.xy;
	vec2 prev_pos = s.zw;
	vec2 vel = current_pos - prev_pos;
	vec2 texel = 1.0 / phys_tex_size;
	vec2 node_coord = floor(UV * (phys_tex_size - vec2(1.0)) + vec2(0.5));
	bool coarse_sample = mod(node_coord.x + node_coord.y, 2.0) > 0.5;
	vec2 right_pos = sample_position(sample_uv + vec2(texel.x, 0.0));
	vec2 down_pos = coarse_sample ? right_pos : sample_position(sample_uv + vec2(0.0, texel.y));
	float rest_dx = grid_size_pixels.x / max(phys_tex_size.x - 1.0, 1.0);
	float rest_dy = grid_size_pixels.y / max(phys_tex_size.y - 1.0, 1.0);
	float stretch_x = length(right_pos - current_pos) / max(rest_dx, 1e-4);
	float stretch_y = coarse_sample
		? stretch_x
		: length(down_pos - current_pos) / max(rest_dy, 1e-4);
	float speed = length(vel);
	float blur_mask = clamp(speed / max(max(grid_size_pixels.x, grid_size_pixels.y) * 0.03, 1.0), 0.0, 1.0);
	VERTEX = current_pos + vel * motion_blur_strength * blur_mask;
	v_energy = speed;
	v_stretch = max((stretch_x + stretch_y) * 0.5, 1e-4);
	v_velocity = vel;
}

vec4 texture_mode_color(vec2 sample_uv, float glow) {
	vec4 energy_color = texture(energy_gradient_tex, vec2(clamp(glow, 0.0, 1.0), 0.5));
	vec4 tex_col = texture(main_tex, clamp(sample_uv, vec2(0.0), vec2(1.0)));
	vec4 base = tex_col * line_color;
	vec4 bright = tex_col * energy_color;
	bright.a = base.a;
	return mix(base, bright, clamp(glow * 1.15, 0.0, 1.0));
}

vec4 grid_mode_color(vec2 sample_uv, float glow) {
	vec4 energy_color = texture(energy_gradient_tex, vec2(clamp(glow, 0.0, 1.0), 0.5));
	vec2 cells = vec2(grid_dims);
	vec2 f = fract(sample_uv * cells);
	vec2 df = fwidth(sample_uv * cells);
	float edge = min(f.x, 1.0 - f.x);
	float edge_y = min(f.y, 1.0 - f.y);

	float stretch_thin = clamp((v_stretch - 1.0) * 0.55 * stretch_taper_strength, 0.0, 0.72);
	float compression_boost = clamp((1.0 - v_stretch) * 0.45 * stretch_taper_strength, 0.0, 0.35);
	float energy_thin = clamp(v_energy * 0.06, 0.0, 0.45);
	float taper = clamp(1.0 - stretch_thin - energy_thin + compression_boost, 0.28, 1.35);
	float core_x = 1.0 - smoothstep(0.0, df.x * 2.0 * taper, edge);
	float core_y = 1.0 - smoothstep(0.0, df.y * 2.0 * taper, edge_y);
	float core = clamp(core_x + core_y, 0.0, 1.0);

	float halo_x = 1.0 - smoothstep(0.0, df.x * 9.0 * taper, edge);
	float halo_y = 1.0 - smoothstep(0.0, df.y * 9.0 * taper, edge_y);
	float halo = clamp((halo_x + halo_y) * 0.25, 0.0, 1.0);

	float line_mask = max(core, halo);
	vec4 bg = vec4(line_color.rgb * 0.08, 0.85);
	vec4 lit = mix(line_color, energy_color, clamp(glow * 1.25, 0.0, 1.0));
	lit.rgb = min(lit.rgb * (1.0 + glow * 0.4), vec3(1.0));
	return mix(bg, lit, line_mask);
}

vec4 display_color(vec2 sample_uv, float glow) {
	if (display_mode == 1)
		return texture_mode_color(sample_uv, glow);
	return grid_mode_color(sample_uv, glow);
}

void fragment() {
	float glow = clamp(pow(v_energy * glow_gain, 2.0), 0.0, 1.0);
	float persistence_glow = clamp(pow(v_energy * glow_gain * (1.0 + persistence * 4.0), 1.35), 0.0, 1.0) * persistence;
	float combined_glow = max(glow, persistence_glow);
	vec4 base = display_color(UV, combined_glow);

	if (persistence > 0.0 && v_energy > 1e-4) {
		vec2 trail_offset = vec2(
			v_velocity.x / max(grid_size_pixels.x, 1.0),
			v_velocity.y / max(grid_size_pixels.y, 1.0)
		);
		float trail_mix = clamp(persistence * smoothstep(0.2, 2.0, v_energy), 0.0, 1.0);
		vec4 trail = display_color(UV - trail_offset * (0.6 + persistence * 2.4), clamp(combined_glow * (1.0 + persistence), 0.0, 1.0));
		base = vec4(max(base.rgb, trail.rgb * trail_mix), max(base.a, trail.a * trail_mix));
	}

	if (chromatic_strength <= 0.0 || v_energy <= CHROMATIC_THRESHOLD) {
		COLOR = base;
		COLOR.rgb = min(COLOR.rgb, vec3(1.0));
		return;
	}

	float split_strength = (v_energy - CHROMATIC_THRESHOLD) * chromatic_strength / max(max(grid_size_pixels.x, grid_size_pixels.y), 1.0);
	vec2 split_offset = vec2(split_strength, split_strength * 0.35);
	vec4 plus = display_color(UV + split_offset, glow);
	vec4 minus = display_color(UV - split_offset, glow);
	float fringe_mix = clamp((v_energy - CHROMATIC_THRESHOLD) * 0.4, 0.0, 1.0);

	vec3 fringed = vec3(plus.r, base.g, minus.b);
	COLOR = vec4(mix(base.rgb, fringed, fringe_mix), max(max(plus.a, base.a), minus.a));
	COLOR.rgb = min(COLOR.rgb, vec3(1.0));
}
