shader_type spatial;

// Table tennis ball with a printed stripe and logo so its spin can be read. Ball.cs rotates the
// mesh with the ball's spin; when it turns a lot within one frame the print is smeared around
// the spin axis like motion blur, and a rim glow shows the kind of spin (warm topspin, cool backspin).

uniform vec4 base_color : source_color = vec4(1.0, 0.98, 0.94, 1.0);
uniform vec4 print_color : source_color = vec4(0.95, 0.35, 0.05, 1.0);
uniform vec3 spin_axis = vec3(1.0, 0.0, 0.0);  // object space, normalised
uniform float blur_angle = 0.0;                // radians the ball turns during one frame
uniform vec4 glow_color : source_color = vec4(1.0, 0.45, 0.1, 1.0);
uniform float glow_strength = 0.0;             // 0..1 from the spin rate

const int BLUR_SAMPLES = 8;

varying vec3 local_dir;

void vertex() {
	local_dir = VERTEX;
}

vec3 rotate_about(vec3 p, vec3 axis, float angle) {
	float c = cos(angle);
	float s = sin(angle);
	return p * c + cross(axis, p) * s + axis * dot(axis, p) * (1.0 - c);
}

float print_mask(vec3 p) {
	float stripe = 1.0 - smoothstep(0.10, 0.16, abs(p.y));
	float logo = 1.0 - smoothstep(0.30, 0.36, distance(p, vec3(0.0, 0.8, 0.6)));
	return max(stripe, logo);
}

void fragment() {
	vec3 p = normalize(local_dir);
	float mask = 0.0;
	for (int i = 0; i < BLUR_SAMPLES; i++) {
		float t = float(i) / float(BLUR_SAMPLES - 1) - 0.5;
		mask += print_mask(rotate_about(p, spin_axis, blur_angle * t));
	}
	mask /= float(BLUR_SAMPLES);

	ALBEDO = mix(base_color.rgb, print_color.rgb, mask);
	ROUGHNESS = 0.35;
	float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 1.2);
	ALBEDO = mix(ALBEDO, glow_color.rgb, rim * glow_strength * 0.6);
	EMISSION = base_color.rgb * 0.15 + glow_color.rgb * rim * glow_strength * 3.0;
}
