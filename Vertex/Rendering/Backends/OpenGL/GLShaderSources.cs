using Vertex.Rendering.Shaders;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// The shaders only the OpenGL backend has: the 2D canvas. The scene and post-processing shaders are shared with
    /// Vulkan; see <see cref="ShaderLibrary"/>.
    /// </summary>
    internal static class GLShaderSources
    {
        // The 2D layer: positions arrive in pixels from the top-left, colours already multiplied by alpha. Each quad can
        // be cut to a smooth-edged rounded rectangle and clipped to a rectangle (see CanvasQuad).
        public const string CanvasVertex = """
            #version 330 core

            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aUV;
            layout (location = 2) in vec4 aColor;
            layout (location = 3) in vec4 aShape;
            layout (location = 4) in vec4 aShapeStyle;
            layout (location = 5) in vec4 aClip;

            uniform vec2 uScreenSize;

            out vec2 vUV;
            out vec4 vColor;
            out vec2 vPixel;
            flat out vec4 vShape;
            flat out vec4 vShapeStyle;
            flat out vec4 vClip;

            void main()
            {
                vec2 ndc = vec2(aPosition.x / uScreenSize.x * 2.0 - 1.0, 1.0 - aPosition.y / uScreenSize.y * 2.0);
                gl_Position = vec4(ndc, 0.0, 1.0);
                vUV = aUV;
                vColor = aColor;
                vPixel = aPosition;
                vShape = aShape;
                vShapeStyle = aShapeStyle;
                vClip = aClip;
            }
            """;

        public const string CanvasFragment = """
            #version 330 core

            in vec2 vUV;
            in vec4 vColor;
            in vec2 vPixel;
            flat in vec4 vShape;
            flat in vec4 vShapeStyle;
            flat in vec4 vClip;

            uniform sampler2D uTexture;

            out vec4 FragColor;

            """ + ShaderLibrary.CanvasShape + """

            void main()
            {
                FragColor = texture(uTexture, vUV) * vColor * canvasCoverage(vPixel, vShape, vShapeStyle, vClip);
            }
            """;
    }
}
