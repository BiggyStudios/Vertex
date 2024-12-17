#version 330

out vec4 outputColor;

in vec3 ourCol;

void main()
{
    outputColor = vec4(ourCol, 1.0);
}