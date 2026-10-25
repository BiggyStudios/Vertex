namespace Vertex.Rendering.OpenGL
{
    internal static class GLShaderSources
    {
        public const int MaxDirectionalLights = 4;
        public const int MaxPointLights = 8;

        public const string LitVertex = """
            #version 330 core

            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aNormal;
            layout (location = 2) in vec2 aUV;

            uniform mat4 uModel;
            uniform mat4 uNormalMatrix;
            uniform mat4 uView;
            uniform mat4 uProjection;

            out vec3 vWorldPosition;
            out vec3 vNormal;
            out vec2 vUV;

            void main()
            {
                vec4 worldPosition = uModel * vec4(aPosition, 1.0);
                vWorldPosition = worldPosition.xyz;
                vNormal = mat3(uNormalMatrix) * aNormal;
                vUV = aUV;
                gl_Position = uProjection * uView * worldPosition;
            }
            """;

        public const string LitFragment = """
            #version 330 core

            #define MAX_DIR_LIGHTS 4
            #define MAX_POINT_LIGHTS 8

            struct DirectionalLight
            {
                vec3 direction;
                vec3 color;
            };

            struct PointLight
            {
                vec3 position;
                vec3 color;
                float range;
            };

            in vec3 vWorldPosition;
            in vec3 vNormal;
            in vec2 vUV;

            uniform vec4 uColor;
            uniform sampler2D uTexture;
            uniform bool uHasTexture;
            uniform float uSpecular;
            uniform float uShininess;

            uniform vec3 uCameraPosition;
            uniform int uDirectionalLightCount;
            uniform DirectionalLight uDirectionalLights[MAX_DIR_LIGHTS];
            uniform int uPointLightCount;
            uniform PointLight uPointLights[MAX_POINT_LIGHTS];

            // Light from the sky (above) and ground (below), and fog. Colours as authored (sRGB).
            uniform vec3 uSkyColor;
            uniform vec3 uHorizonColor;
            uniform vec3 uGroundColor;
            uniform float uAmbientIntensity;
            uniform vec3 uFogColor;
            uniform float uFogDensity;
            uniform float uExposure;

            // The sun's shadow map. uShadowLightIndex is -1 when nothing casts shadows.
            uniform int uShadowLightIndex;
            uniform mat4 uLightSpace;
            uniform sampler2DShadow uShadowMap;
            uniform float uShadowTexelSize;
            uniform float uShadowNormalOffset;

            out vec4 FragColor;

            vec3 diffuseLight;
            vec3 specularLight;

            // Colours are picked on screen (sRGB), but light adds up properly in linear values. Convert in, light, convert out.
            vec3 toLinear(vec3 color) { return pow(color, vec3(2.2)); }
            vec3 toScreen(vec3 color) { return pow(color, vec3(1.0 / 2.2)); }

            // ACES filmic curve (Narkowicz's fit): bright areas roll off smoothly instead of clipping to flat white.
            vec3 toneMap(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }

            void addLight(vec3 N, vec3 V, vec3 L, vec3 color)
            {
                float NdotL = max(dot(N, L), 0.0);
                if (NdotL <= 0.0) return;

                vec3 H = normalize(L + V);
                diffuseLight += color * NdotL;
                specularLight += color * pow(max(dot(N, H), 0.0), uShininess) * uSpecular;
            }

            // 1 in full sunlight, 0 in shadow, in between at soft edges.
            float sunShadow(vec3 N, vec3 L)
            {
                // Look up from slightly off the surface (more where it faces away from the sun), so it doesn't shadow itself.
                float facing = clamp(dot(N, L), 0.0, 1.0);
                vec3 position = vWorldPosition + N * uShadowNormalOffset * (2.0 - facing);
                vec4 lightSpace = uLightSpace * vec4(position, 1.0);
                vec3 p = lightSpace.xyz / lightSpace.w;

                vec2 uv = p.xy * 0.5 + 0.5;
                if (uv.x <= 0.0 || uv.x >= 1.0 || uv.y <= 0.0 || uv.y >= 1.0 || p.z >= 1.0) return 1.0;

                // The projection gives depth from 0 to 1, which OpenGL stores as 0.5 to 1.
                float depth = p.z * 0.5 + 0.5 - 0.0002;

                // Average 3x3 nearby samples (each already blended from 4 by the hardware) for soft edges.
                float lit = 0.0;
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        lit += texture(uShadowMap, vec3(uv + vec2(x, y) * uShadowTexelSize, depth));
                    }
                }
                lit /= 9.0;

                // Fade out towards the edge of the shadowed area rather than stopping at a hard line.
                float edge = max(abs(p.x), abs(p.y));
                return mix(lit, 1.0, smoothstep(0.85, 1.0, edge));
            }

            void main()
            {
                vec4 albedo = uColor;
                if (uHasTexture) albedo *= texture(uTexture, vUV);
                vec3 baseColor = toLinear(albedo.rgb);

                vec3 N = normalize(vNormal);
                vec3 V = normalize(uCameraPosition - vWorldPosition);

                // Soft light from all around: the sky on surfaces facing up, ground bounce on those facing down. A surface
                // facing up sees the whole sky, which is mostly paler than overhead, so it gets a sky/horizon blend.
                vec3 skyLight = toLinear(mix(uSkyColor, uHorizonColor, 0.5));
                diffuseLight = mix(toLinear(uGroundColor), skyLight, N.y * 0.5 + 0.5) * uAmbientIntensity;
                specularLight = vec3(0.0);

                for (int i = 0; i < uDirectionalLightCount; i++)
                {
                    vec3 L = normalize(-uDirectionalLights[i].direction);
                    vec3 color = uDirectionalLights[i].color;
                    if (i == uShadowLightIndex) color *= sunShadow(N, L);
                    addLight(N, V, L, color);
                }

                for (int i = 0; i < uPointLightCount; i++)
                {
                    vec3 toLight = uPointLights[i].position - vWorldPosition;
                    float distance = length(toLight);
                    float falloff = clamp(1.0 - distance / uPointLights[i].range, 0.0, 1.0);
                    addLight(N, V, toLight / distance, uPointLights[i].color * falloff * falloff);
                }

                vec3 color = toScreen(toneMap((baseColor * diffuseLight + specularLight) * uExposure));

                // Fade into the horizon with distance. Done last, so far-off things match the sky exactly.
                float distance = length(uCameraPosition - vWorldPosition);
                float fog = 1.0 - exp(-pow(uFogDensity * distance, 2.0));
                FragColor = vec4(mix(color, uFogColor, fog), albedo.a);
            }
            """;

        // Draws depth only, as seen from the sun, to find what's in shadow.
        public const string ShadowVertex = """
            #version 330 core

            layout (location = 0) in vec3 aPosition;

            uniform mat4 uModel;
            uniform mat4 uLightSpace;

            void main()
            {
                gl_Position = uLightSpace * uModel * vec4(aPosition, 1.0);
            }
            """;

        public const string ShadowFragment = """
            #version 330 core

            void main()
            {
            }
            """;

        // One triangle covering the screen; each pixel works out which way it looks and colours itself by height.
        public const string SkyVertex = """
            #version 330 core

            out vec2 vScreen;

            void main()
            {
                vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
                vScreen = corner;
                gl_Position = vec4(corner, 1.0, 1.0);
            }
            """;

        public const string SkyFragment = """
            #version 330 core

            in vec2 vScreen;

            uniform mat4 uInverseViewProjection;
            uniform vec3 uSkyColor;
            uniform vec3 uHorizonColor;
            uniform vec3 uGroundColor;
            uniform vec3 uSunDirection;
            uniform vec3 uSunColor;
            uniform bool uHasSun;

            out vec4 FragColor;

            void main()
            {
                vec4 near = uInverseViewProjection * vec4(vScreen, 0.0, 1.0);
                vec4 far = uInverseViewProjection * vec4(vScreen, 1.0, 1.0);
                vec3 direction = normalize(far.xyz / far.w - near.xyz / near.w);

                // Pale at the horizon, deeper overhead; below the horizon, a darker band towards the ground colour.
                float height = direction.y;
                vec3 color = height >= 0.0
                    ? mix(uHorizonColor, uSkyColor, pow(height, 0.55))
                    : mix(uHorizonColor, mix(uHorizonColor, uGroundColor, 0.6), clamp(-height * 4.0, 0.0, 1.0));

                // The sun: a bright disc and a soft glow around it.
                if (uHasSun)
                {
                    float towardsSun = max(dot(direction, uSunDirection), 0.0);
                    color += uSunColor * (pow(towardsSun, 900.0) * 3.0 + pow(towardsSun, 12.0) * 0.18);
                }

                FragColor = vec4(color, 1.0);
            }
            """;

        public const string UnlitFragment = """
            #version 330 core

            in vec2 vUV;

            uniform vec4 uColor;
            uniform sampler2D uTexture;
            uniform bool uHasTexture;

            out vec4 FragColor;

            void main()
            {
                vec4 color = uColor;
                if (uHasTexture) color *= texture(uTexture, vUV);
                FragColor = color;
            }
            """;

        // The 2D layer: positions arrive in pixels from the top-left, colours already multiplied by alpha.
        public const string CanvasVertex = """
            #version 330 core

            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aUV;
            layout (location = 2) in vec4 aColor;

            uniform vec2 uScreenSize;

            out vec2 vUV;
            out vec4 vColor;

            void main()
            {
                vec2 ndc = vec2(aPosition.x / uScreenSize.x * 2.0 - 1.0, 1.0 - aPosition.y / uScreenSize.y * 2.0);
                gl_Position = vec4(ndc, 0.0, 1.0);
                vUV = aUV;
                vColor = aColor;
            }
            """;

        public const string CanvasFragment = """
            #version 330 core

            in vec2 vUV;
            in vec4 vColor;

            uniform sampler2D uTexture;

            out vec4 FragColor;

            void main()
            {
                FragColor = texture(uTexture, vUV) * vColor;
            }
            """;
    }
}
