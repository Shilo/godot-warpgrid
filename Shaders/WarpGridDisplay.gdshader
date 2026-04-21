shader_type canvas_item;
render_mode unshaded;

uniform sampler2D positions_texture : filter_nearest, repeat_disable;
uniform vec4 line_color : source_color = vec4(0.14, 0.81, 0.96, 1.0);

void vertex() {
    VERTEX = textureLod(positions_texture, UV, 0.0).rg;
}

void fragment() {
    COLOR = line_color;
}
