shader_type spatial;
render_mode cull_disabled;

// Wood for the blade and handle, from the mesh's local position: long grain running down the
// paddle (along Y) on the faces, the glued plies as thin dark lines on the edge, and an
// optional veneer stripe (the gold strip along the handle).

uniform vec4 light_wood : source_color = vec4(0.9, 0.8, 0.62, 1.0);
uniform vec4 dark_wood : source_color = vec4(0.76, 0.6, 0.42, 1.0);
uniform float grain_density = 700.0;     // grain lines per metre across the paddle
uniform float roughness = 0.6;

uniform float half_thickness = 0.0029;   // plies show on the edge when ply_count > 0
uniform float ply_count = 0.0;
uniform vec4 ply_line : source_color = vec4(0.5, 0.36, 0.22, 1.0);

uniform float stripe_width = 0.0;        // half width at the start, 0 = no stripe
uniform float stripe_end_width = 0.0;    // half width at the end, if wider than at the start
uniform vec2 stripe_start = vec2(0.0);
uniform vec2 stripe_end = vec2(0.0);
uniform vec4 stripe_color : source_color = vec4(0.92, 0.72, 0.25, 1.0);

varying vec3 local_pos;
varying vec3 local_normal;

void vertex() {
	local_pos = VERTEX;
	local_normal = NORMAL;
}

float hash(float n) {
	return fract(sin(n) * 43758.5453);
}

float noise(float x) {
	float i = floor(x);
	float f = fract(x);
	return mix(hash(i), hash(i + 1.0), f * f * (3.0 - 2.0 * f));
}

void fragment() {
	vec3 p = local_pos;

	// Fine streaks, gently wandering along the length, with broader tone variation.
	float across = p.x * grain_density + noise(p.y * 30.0) * 3.0 + sin(p.y * 55.0 + p.x * 90.0) * 0.6;
	float streak = pow(noise(across), 4.0);
	float tone = noise(p.x * 60.0 + 7.0) * 0.5;
	vec3 colour = mix(light_wood.rgb, dark_wood.rgb, clamp(streak * 0.8 + tone * 0.4, 0.0, 1.0));

	// Edge: thin glue lines between the plies.
	bool on_edge = abs(normalize(local_normal).z) < 0.5;
	if (ply_count > 0.0 && on_edge) {
		float layer = (p.z / half_thickness * 0.5 + 0.5) * ply_count;
		float glue = 1.0 - smoothstep(0.04, 0.1, abs(fract(layer + 0.5) - 0.5));
		colour = mix(colour * 0.93, ply_line.rgb, glue);
	}

	if (stripe_width > 0.0) {
		vec2 a = stripe_start;
		vec2 ab = stripe_end - a;
		float t = clamp(dot(p.xy - a, ab) / dot(ab, ab), 0.0, 1.0);
		float d = distance(p.xy, a + ab * t);
		float w = mix(stripe_width, max(stripe_end_width, stripe_width), t);
		colour = mix(colour, stripe_color.rgb, 1.0 - smoothstep(w * 0.8, w, d));
	}

	ALBEDO = colour;
	ROUGHNESS = roughness;
}
