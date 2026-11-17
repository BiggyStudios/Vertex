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
            uniform vec3 uEmission;
            uniform float uEmissionStrength;

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

            // 0 for smooth lighting, 1 for toon bands; see Lighting.CelShading.
            uniform float uCelShading;

            // The sun's shadow map. uShadowLightIndex is -1 when nothing casts shadows.
            uniform int uShadowLightIndex;
            uniform mat4 uLightSpace;
            uniform sampler2DShadow uShadowMap;
            uniform float uShadowTexelSize;
            uniform float uShadowNormalOffset;

            layout (location = 0) out vec4 FragColor;
            // What glows, in linear light, for bloom. Everything drawn into the scene writes it, so nearer things hide glow behind them.
            layout (location = 1) out vec4 GlowColor;

            vec3 diffuseLight;
            vec3 specularLight;

            // Colours are picked on screen (sRGB), but light adds up properly in linear values. Convert in, light, convert out.
            vec3 toLinear(vec3 color) { return pow(color, vec3(2.2)); }
            vec3 toScreen(vec3 color) { return pow(color, vec3(1.0 / 2.2)); }

            // ACES filmic curve (Narkowicz's fit): bright areas roll off smoothly instead of clipping to flat white.
            vec3 toneMap(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }

            // Toon lighting: three flat steps (unlit, half lit, fully lit) with soft edges between them, rather than a smooth fade.
            float toonBands(float NdotL) { return smoothstep(0.0, 0.08, NdotL) * 0.45 + smoothstep(0.35, 0.45, NdotL) * 0.55; }

            void addLight(vec3 N, vec3 V, vec3 L, vec3 color)
            {
                float NdotL = max(dot(N, L), 0.0);
                if (NdotL <= 0.0) return;

                // With cel shading, the light falls into bands, and the highlight is a crisp spot instead of a soft fade.
                vec3 H = normalize(L + V);
                float highlight = pow(max(dot(N, H), 0.0), uShininess);
                diffuseLight += color * mix(NdotL, toonBands(NdotL), uCelShading);
                specularLight += color * mix(highlight, smoothstep(0.6, 0.7, highlight), uCelShading) * uSpecular;
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

                // Emission shines whatever the lighting, and also glows out as bloom.
                vec3 emission = toLinear(uEmission) * uEmissionStrength;
                vec3 color = toScreen(toneMap((baseColor * diffuseLight + specularLight + emission) * uExposure));

                // Fade into the horizon with distance. Done last, so far-off things match the sky exactly.
                float distance = length(uCameraPosition - vWorldPosition);
                float fog = 1.0 - exp(-pow(uFogDensity * distance, 2.0));
                FragColor = vec4(mix(color, uFogColor, fog), albedo.a);
                GlowColor = vec4(emission * (1.0 - fog), 1.0);
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

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 GlowColor;

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

                // The sun: a bright disc and a soft glow around it. The disc blooms a little too.
                vec3 glow = vec3(0.0);
                if (uHasSun)
                {
                    float towardsSun = max(dot(direction, uSunDirection), 0.0);
                    float disc = pow(towardsSun, 900.0) * 3.0;
                    color += uSunColor * (disc + pow(towardsSun, 12.0) * 0.18);
                    glow = uSunColor * disc * 0.15;
                }

                FragColor = vec4(color, 1.0);
                GlowColor = vec4(glow, 1.0);
            }
            """;

        public const string UnlitFragment = """
            #version 330 core

            in vec3 vWorldPosition;
            in vec2 vUV;

            uniform vec4 uColor;
            uniform sampler2D uTexture;
            uniform bool uHasTexture;
            uniform vec3 uEmission;
            uniform float uEmissionStrength;

            uniform vec3 uCameraPosition;
            uniform vec3 uFogColor;
            uniform float uFogDensity;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 GlowColor;

            void main()
            {
                vec4 color = uColor;
                if (uHasTexture) color *= texture(uTexture, vUV);

                // Fades into the horizon with distance, as lit things do, glow and all.
                float distance = length(uCameraPosition - vWorldPosition);
                float fog = 1.0 - exp(-pow(uFogDensity * distance, 2.0));
                FragColor = vec4(mix(color.rgb, uFogColor, fog), color.a);
                GlowColor = vec4(pow(uEmission, vec3(2.2)) * uEmissionStrength * (1.0 - fog), 1.0);
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

        // Particles: camera-facing quads made on the CPU, with straight (not premultiplied) colours.
        public const string ParticleVertex = """
            #version 330 core

            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec2 aCorner;
            layout (location = 2) in vec4 aColor;
            layout (location = 3) in float aGlow;
            layout (location = 4) in float aAdditive;

            uniform mat4 uView;
            uniform mat4 uProjection;
            uniform vec3 uCameraPosition;
            uniform float uFogDensity;

            out vec2 vCorner;
            out vec4 vColor;
            out float vGlow;
            out float vAdditive;
            out float vFog;

            void main()
            {
                vCorner = aCorner;
                vColor = aColor;
                vGlow = aGlow;
                vAdditive = aAdditive;
                vFog = 1.0 - exp(-pow(uFogDensity * length(uCameraPosition - aPosition), 2.0));
                gl_Position = uProjection * uView * vec4(aPosition, 1.0);
            }
            """;

        // A soft round dot, blended with premultiplied alpha: an alpha of 0 adds light, anything else covers what's behind.
        public const string ParticleFragment = """
            #version 330 core

            in vec2 vCorner;
            in vec4 vColor;
            in float vGlow;
            in float vAdditive;
            in float vFog;

            uniform vec3 uFogColor;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 GlowColor;

            void main()
            {
                float r2 = dot(vCorner, vCorner);
                if (r2 >= 1.0) discard;

                float shape = (1.0 - r2) * (1.0 - r2);
                float alpha = vColor.a * shape;
                float covers = alpha * (1.0 - vAdditive);

                // In the distance, dust fades into the fog and glowing things fade out.
                vec3 color = mix(vColor.rgb, uFogColor, vFog * (1.0 - vAdditive)) * mix(1.0, 1.0 - vFog, vAdditive);
                FragColor = vec4(color * alpha, covers);
                GlowColor = vec4(pow(vColor.rgb, vec3(2.2)) * vGlow * alpha * (1.0 - vFog), covers);
            }
            """;

        // One triangle covering the screen, for the passes that work on whole images.
        public const string FullscreenVertex = """
            #version 330 core

            out vec2 vUV;

            void main()
            {
                vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
                vUV = corner * 0.5 + 0.5;
                gl_Position = vec4(corner, 0.0, 1.0);
            }
            """;

        // Halves the glow image, averaging 13 nearby samples (Jimenez's filter from Call of Duty: Advanced Warfare),
        // which blurs it smoothly without flickering as things move.
        public const string BloomDownsampleFragment = """
            #version 330 core

            in vec2 vUV;

            uniform sampler2D uSource;
            uniform vec2 uTexelSize;

            out vec4 FragColor;

            vec3 tap(float x, float y) { return texture(uSource, vUV + vec2(x, y) * uTexelSize).rgb; }

            void main()
            {
                vec3 a = tap(-2.0, 2.0), b = tap(0.0, 2.0), c = tap(2.0, 2.0);
                vec3 d = tap(-2.0, 0.0), e = tap(0.0, 0.0), f = tap(2.0, 0.0);
                vec3 g = tap(-2.0, -2.0), h = tap(0.0, -2.0), i = tap(2.0, -2.0);
                vec3 j = tap(-1.0, 1.0), k = tap(1.0, 1.0), l = tap(-1.0, -1.0), m = tap(1.0, -1.0);

                vec3 color = e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125;
                FragColor = vec4(max(color, vec3(0.0)), 1.0);
            }
            """;

        // Doubles a blurred glow image back up with a 3x3 tent filter, added onto the next size up.
        public const string BloomUpsampleFragment = """
            #version 330 core

            in vec2 vUV;

            uniform sampler2D uSource;
            uniform vec2 uTexelSize;

            out vec4 FragColor;

            vec3 tap(float x, float y) { return texture(uSource, vUV + vec2(x, y) * uTexelSize).rgb; }

            void main()
            {
                vec3 color = tap(0.0, 0.0) * 4.0
                    + (tap(-1.0, 0.0) + tap(1.0, 0.0) + tap(0.0, -1.0) + tap(0.0, 1.0)) * 2.0
                    + tap(-1.0, -1.0) + tap(1.0, -1.0) + tap(-1.0, 1.0) + tap(1.0, 1.0);
                FragColor = vec4(color / 16.0, 1.0);
            }
            """;

        // The finished scene with the bloom on top, blended like light on a lens: it brightens towards white but never
        // past it, so the scene underneath keeps its colours. First, the outlines darken the scene along the edges of
        // things, found from its depth.
        public const string CompositeFragment = """
            #version 330 core

            in vec2 vUV;

            uniform sampler2D uScene;
            uniform sampler2D uBloom;
            uniform float uBloomStrength;

            uniform sampler2D uDepth;
            uniform float uOutlines;
            uniform float uProjectionZ;
            uniform float uProjectionW;
            uniform vec2 uOutlineStep;

            out vec4 FragColor;

            // One over the distance to what's drawn there. From the depth d stored for it, NDC depth is 2d - 1, which for
            // the projection is M43 / distance - M33; so 1 / distance is (NDC + M33) / M43. Across any flat surface it
            // changes evenly from pixel to pixel, which is what finds the edges below.
            float nearness(vec2 uv) { return (texture(uDepth, uv).r * 2.0 - 1.0 + uProjectionZ) / uProjectionW; }

            // How much this pixel is on an edge: the outline of something against what's behind it, or a corner. On a flat
            // surface, the pixels either side average out to this one; where it's nearer or further than that average, it's
            // an edge, so the line takes a pixel either side of it. Relative to its own nearness, so it's the same near or far.
            float edge()
            {
                float centre = nearness(vUV);
                vec2 x = vec2(uOutlineStep.x, 0.0);
                vec2 y = vec2(0.0, uOutlineStep.y);
                float across = 2.0 * centre - nearness(vUV + x) - nearness(vUV - x);
                float down = 2.0 * centre - nearness(vUV + y) - nearness(vUV - y);
                float strength = max(abs(across), abs(down)) / max(centre, 1e-6);

                // Faint ones are left out, and outlines fade away into the distance, where they'd only be noise.
                float distance = 1.0 / max(centre, 1e-6);
                return smoothstep(0.003, 0.02, strength) * (1.0 - smoothstep(25.0, 80.0, distance));
            }

            void main()
            {
                vec3 scene = pow(texture(uScene, vUV).rgb, vec3(2.2));
                // Darkened by as much as it looks on screen, not in linear light, where the same cut looks far less.
                if (uOutlines > 0.0) scene *= pow(1.0 - 0.85 * uOutlines * edge(), 2.2);
                vec3 bloom = 1.0 - exp(-texture(uBloom, vUV).rgb * uBloomStrength);
                vec3 color = 1.0 - (1.0 - scene) * (1.0 - bloom);
                FragColor = vec4(pow(color, vec3(1.0 / 2.2)), 1.0);
            }
            """;
    }
}
