namespace Vertex.Rendering.Shaders
{
    /// <summary>Which graphics API a shader is being built for.</summary>
    internal enum ShaderTarget
    {
        OpenGL,
        Vulkan
    }

    /// <summary>
    /// The scene and post-processing shaders, written once for both backends. <see cref="Build"/> puts a header in front
    /// that bridges the APIs: OpenGL 3.3 has no descriptor sets, push constants or locations on what passes between
    /// stages, and stores depth (and reads its images) differently. Each backend binds the same data the same way:
    /// <see cref="FrameUniforms"/> at set 0 binding 0, <see cref="MaterialUniforms"/> at set 1, and <see cref="PostUniforms"/>
    /// as push constants (a uniform struct called <c>post</c> in OpenGL).
    /// <para>
    /// Lighting is done in linear light and kept there (in half-float images) until the end, so very bright things stay
    /// bright: they bloom, and tone mapping rolls them off smoothly. Colours the game picks are sRGB; the CPU converts
    /// them before they get here.
    /// </para>
    /// </summary>
    internal static class ShaderLibrary
    {
        public const int MaxDirectionalLights = 4;
        public const int MaxPointLights = 8;
        public const int MaxCascades = 4;

        /// <summary>
        /// How much of a canvas pixel a quad covers, 0 to 1: its rounded-rectangle shape (a smooth edge, so curves don't
        /// look jagged) and its clip rectangle. Shared by both backends' canvas shaders; see <see cref="CanvasQuad"/>.
        /// </summary>
        public const string CanvasShape = """
            float roundedBox(vec2 p, vec2 halfSize, float radius)
            {
                radius = min(radius, min(halfSize.x, halfSize.y));
                vec2 q = abs(p) - halfSize + radius;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
            }

            float canvasCoverage(vec2 pixel, vec4 shape, vec4 style, vec4 clip)
            {
                if (clip.z > clip.x && (pixel.x < clip.x || pixel.y < clip.y || pixel.x >= clip.z || pixel.y >= clip.w)) return 0.0;
                if (shape.z <= 0.0) return 1.0;

                float distance = roundedBox(pixel - shape.xy, shape.zw, style.x);
                // An outline: only the band just inside the edge.
                if (style.y > 0.0) distance = abs(distance + style.y * 0.5) - style.y * 0.5;
                float softness = max(style.z, 0.001);
                return 1.0 - smoothstep(-softness * 0.5, softness * 0.5, distance);
            }
            """;

        public static string Build(ShaderTarget target, params string[] parts) =>
            (target == ShaderTarget.Vulkan ? VulkanHeader : OpenGLHeader) + string.Concat(parts);

        private const string VulkanHeader = """
            #version 450
            #define VERTEX_VULKAN 1
            #define UNIFORM_BLOCK(s, b) layout (set = s, binding = b, std140) uniform
            #define TEXTURE(s, b) layout (set = s, binding = b) uniform
            #define VARYING(n) layout (location = n)
            #define VERTEX_ID gl_VertexIndex
            // Depth is stored as the projection gives it, 0 to 1.
            #define NDC_DEPTH(stored) (stored)
            #define STORED_DEPTH(ndc) (ndc)
            // 3D passes use a flipped viewport, so the first row of what they draw is the top of the view (+Y).
            #define UV_TO_NDC(uv) vec2((uv).x * 2.0 - 1.0, 1.0 - (uv).y * 2.0)
            #define NDC_TO_UV(ndc) vec2((ndc).x * 0.5 + 0.5, 0.5 - (ndc).y * 0.5)

            """;

        private const string OpenGLHeader = """
            #version 330 core
            #define UNIFORM_BLOCK(s, b) layout (std140) uniform
            #define TEXTURE(s, b) uniform
            #define VARYING(n)
            #define VERTEX_ID gl_VertexID
            // OpenGL stores the projection's 0 to 1 as 0.5 to 1.
            #define NDC_DEPTH(stored) ((stored) * 2.0 - 1.0)
            #define STORED_DEPTH(ndc) ((ndc) * 0.5 + 0.5)
            // Images start at the bottom row.
            #define UV_TO_NDC(uv) ((uv) * 2.0 - 1.0)
            #define NDC_TO_UV(ndc) ((ndc) * 0.5 + 0.5)

            """;

        // Everything about the view being drawn. Matches FrameUniforms.
        public const string Frame = """
            UNIFORM_BLOCK(0, 0) FrameData
            {
                mat4 view;
                mat4 projection;
                mat4 inverseViewProjection;
                mat4 shadowMatrices[4];
                vec4 cascadeSplits;     // how far ahead of the camera each cascade reaches
                vec4 cascadeTexels;     // the world size of one shadow-map texel in each
                vec4 shadow;            // x: 1 / shadow map size, y: index of the light casting shadows (-1 for none), z: cascades
                vec4 cameraPosition;    // w: seconds since the game started
                vec4 cameraForward;
                vec4 skyLight;          // light from above (linear); w: ambient intensity
                vec4 groundLight;       // light from below, the ground lit by the sky and sun; w: fog density
                vec4 horizon;           // the sky at the horizon; w: cel shading
                vec4 zenith;            // the gradient sky overhead; w: sky kind (0 none, 1 gradient, 2 atmosphere)
                vec4 groundColor;       // the ground's own colour; w: cloud cover
                vec4 sunDirection;      // towards the sun; w: 1 if there is one
                vec4 sunColor;          // w: how bright the atmosphere is for it
                vec4 clearColor;        // what's behind everything with no sky
                ivec4 lightCounts;      // x: directional, y: point
                vec4 directionalDirection[4];
                vec4 directionalColor[4];
                vec4 pointPositionRange[8];
                vec4 pointColor[8];
            } frame;

            TEXTURE(0, 1) sampler2DArrayShadow shadowMap;

            const float PI = 3.14159265;

            vec3 toLinear(vec3 color) { return pow(max(color, vec3(0.0)), vec3(2.2)); }

            // ---- The sky ----------------------------------------------------------------------------------------
            // Sunlight scattered by the air on its way through: blue light far more than red (Rayleigh), and haze
            // mostly forward, round the sun (Mie). Looking up there's little air, so the sky is deep blue; towards the
            // horizon the view crosses so much that every colour is scattered, and it pales. Low sun crosses a lot
            // too, losing its blue on the way, so sunsets go orange. Atmosphere.cs does the same on the CPU.
            const vec3 RAYLEIGH = vec3(5.802e-6, 13.558e-6, 33.1e-6) * 8000.0;   // optical depth straight up
            const float MIE = 3.996e-6 * 1200.0;
            const float MIE_EXTINCTION = 4.44e-6 * 1200.0;
            // Ozone, high up, scatters nothing but takes out oranges and greens from light crossing a lot of it: why
            // the sky overhead stays blue at sunset instead of going yellow.
            const vec3 OZONE = vec3(0.650e-6, 1.881e-6, 0.085e-6) * 15000.0;

            // How many times more air there is looking this far up from the horizon than straight up (Kasten and Young).
            float airMass(float cosZenith)
            {
                float zenith = min(degrees(acos(clamp(cosZenith, 0.0, 1.0))), 90.0);
                return 1.0 / (max(cosZenith, 0.0) + 0.50572 * pow(96.07995 - zenith, -1.6364));
            }

            // How much sunlight is left by the time it reaches air seen at this height, of each colour.
            vec3 sunTransmittance(float viewHeight)
            {
                float sunHeight = frame.sunDirection.y;
                // Light reaching the air high up, which is what's seen overhead, has crossed less of it.
                float path = airMass(sunHeight) * mix(1.0, 0.35, clamp(viewHeight, 0.0, 1.0));
                return exp(-(RAYLEIGH + MIE_EXTINCTION + OZONE) * path) * smoothstep(-0.1, 0.03, sunHeight);
            }

            vec3 atmosphere(vec3 direction)
            {
                if (frame.sunDirection.w < 0.5) return frame.horizon.rgb;

                float cosTheta = dot(direction, frame.sunDirection.xyz);
                float viewHeight = max(direction.y, 0.0);
                vec3 extinction = RAYLEIGH + MIE_EXTINCTION + OZONE;

                float rayleighPhase = 0.0596831 * (1.0 + cosTheta * cosTheta);
                const float g = 0.76;
                float miePhase = 0.1193662 * (1.0 - g * g) * (1.0 + cosTheta * cosTheta) /
                                 ((2.0 + g * g) * pow(1.0 + g * g - 2.0 * g * cosTheta, 1.5));

                vec3 scattering = RAYLEIGH * rayleighPhase + MIE * miePhase;
                vec3 inscattered = scattering / extinction * (1.0 - exp(-extinction * airMass(viewHeight)));
                return inscattered * sunTransmittance(viewHeight) * frame.sunColor.rgb * frame.sunColor.w;
            }

            // What the sky looks like in a direction, without the sun's disc or the clouds: for the sky itself, for
            // reflections of it, and for fog.
            vec3 skyRadiance(vec3 direction)
            {
                int kind = int(frame.zenith.w + 0.5);
                if (kind == 0) return frame.clearColor.rgb;

                if (kind == 1)
                {
                    if (direction.y >= 0.0) return mix(frame.horizon.rgb, frame.zenith.rgb, pow(direction.y, 0.55));
                    return mix(frame.horizon.rgb, mix(frame.horizon.rgb, frame.groundColor.rgb, 0.6), clamp(-direction.y * 4.0, 0.0, 1.0));
                }

                if (direction.y >= 0.0) return atmosphere(direction);

                // Below the horizon: the ground far off, through the haze at the horizon.
                vec3 level = normalize(vec3(direction.x, 0.0, direction.z) + vec3(1e-5, 0.0, 0.0));
                return mix(atmosphere(level), frame.groundLight.rgb, smoothstep(0.0, 0.3, -direction.y));
            }

            // What distant things fade into, looking that way: the sky just above the horizon there, with the glow
            // round the sun when looking towards it.
            vec3 fogColor(vec3 direction)
            {
                int kind = int(frame.zenith.w + 0.5);
                if (kind == 0) return frame.clearColor.rgb;
                if (kind == 1) return frame.horizon.rgb;
                return atmosphere(normalize(vec3(direction.x, max(direction.y, 0.03), direction.z)));
            }

            // How much glowing things add to bloom, on top of what they show.
            const float GLOW_STRENGTH = 2.0;

            float fogAmount(float distance)
            {
                return 1.0 - exp(-pow(frame.groundLight.w * distance, 2.0));
            }

            // Soft light from all around: the sky's from above, the ground's from below.
            vec3 ambientLight(vec3 normal)
            {
                return mix(frame.groundLight.rgb, frame.skyLight.rgb, normal.y * 0.5 + 0.5);
            }

            """;

        // A surface's look. Matches MaterialUniforms.
        public const string Material = """
            UNIFORM_BLOCK(1, 0) MaterialData
            {
                vec4 color;          // linear; a: alpha
                vec4 emission;       // linear, times its strength
                vec4 surface;        // x: roughness, y: metallic, z: specular, w: 1 if textured
                vec4 uvTransform;    // xy: scale, zw: offset
                vec4 normalParams;   // x: 1 if there's a normal map, y: its strength
            } material;

            TEXTURE(1, 1) sampler2D materialTexture;
            TEXTURE(1, 2) sampler2D normalMap;

            """;

        private const string DrawConstants = """
            #ifdef VERTEX_VULKAN
            layout (push_constant) uniform DrawBlock { mat4 model; mat4 normalMatrix; } draw;
            #else
            struct DrawBlock { mat4 model; mat4 normalMatrix; };
            uniform DrawBlock draw;
            #endif

            """;

        public const string MeshVertex = Frame + DrawConstants + """
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aNormal;
            layout (location = 2) in vec2 aUV;

            VARYING(0) out vec3 vWorldPosition;
            VARYING(1) out vec3 vNormal;
            VARYING(2) out vec2 vUV;

            void main()
            {
                vec4 worldPosition = draw.model * vec4(aPosition, 1.0);
                vWorldPosition = worldPosition.xyz;
                vNormal = mat3(draw.normalMatrix) * aNormal;
                vUV = aUV;
                gl_Position = frame.projection * frame.view * worldPosition;
            }
            """;

        public const string LitFragment = Frame + Material + """
            VARYING(0) in vec3 vWorldPosition;
            VARYING(1) in vec3 vNormal;
            VARYING(2) in vec2 vUV;

            layout (location = 0) out vec4 FragColor;
            // The part of the colour that's soft light from all around, which ambient occlusion takes away from.
            layout (location = 1) out vec4 AmbientColor;
            // What glows, in linear light, for bloom: emission, so glowing things keep their colour on screen and still
            // light up the space round them. Nearer things cover glow behind them.
            layout (location = 2) out vec4 GlowColor;

            vec3 diffuseLight;
            vec3 specularLight;

            // ---- Shadows ----------------------------------------------------------------------------------------
            // A 5x5 tent filter from 9 lookups, each already blending 4 texels' tests in hardware (Castaño's), so
            // edges are soft and smooth without noise.
            float filteredShadow(vec2 uv, float layer, float depth)
            {
                float texel = frame.shadow.x;
                vec2 position = uv / texel;
                vec2 base = floor(position + 0.5);
                float s = position.x + 0.5 - base.x;
                float t = position.y + 0.5 - base.y;
                base = (base - 0.5) * texel;

                vec3 uw = vec3(4.0 - 3.0 * s, 7.0, 1.0 + 3.0 * s);
                vec3 u = vec3((3.0 - 2.0 * s) / uw.x - 2.0, (3.0 + s) / uw.y, s / uw.z + 2.0) * texel;
                vec3 vw = vec3(4.0 - 3.0 * t, 7.0, 1.0 + 3.0 * t);
                vec3 v = vec3((3.0 - 2.0 * t) / vw.x - 2.0, (3.0 + t) / vw.y, t / vw.z + 2.0) * texel;

                float sum = 0.0;
                for (int y = 0; y < 3; y++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        sum += uw[x] * vw[y] * texture(shadowMap, vec4(base + vec2(u[x], v[y]), layer, depth));
                    }
                }
                return sum / 144.0;
            }

            float cascadeShadow(int cascade, vec3 N, vec3 L)
            {
                // Looked up from a little off the surface, more where it faces away from the sun, so it doesn't
                // shadow itself; by a texel or so of this cascade, as they get bigger further out.
                float facing = clamp(dot(N, L), 0.0, 1.0);
                float texel = frame.cascadeTexels[cascade];
                vec3 position = vWorldPosition + N * texel * (1.0 + 1.5 * (1.0 - facing)) + L * texel;

                vec4 lightSpace = frame.shadowMatrices[cascade] * vec4(position, 1.0);
                vec3 p = lightSpace.xyz / lightSpace.w;
                vec2 uv = NDC_TO_UV(p.xy);
                if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || p.z >= 1.0) return 1.0;

                return filteredShadow(uv, float(cascade), STORED_DEPTH(p.z) - 0.0002);
            }

            // 1 in full sunlight, 0 in shadow. Each cascade covers further out than the one before, at lower detail;
            // they blend into each other, and the last fades out at the shadow distance.
            float sunShadow(vec3 N, vec3 L)
            {
                int cascades = int(frame.shadow.z);
                float depth = dot(vWorldPosition - frame.cameraPosition.xyz, frame.cameraForward.xyz);

                int cascade = cascades;
                for (int i = 0; i < cascades; i++)
                {
                    if (depth < frame.cascadeSplits[i]) { cascade = i; break; }
                }
                if (cascade >= cascades) return 1.0;

                float lit = cascadeShadow(cascade, N, L);

                float start = cascade == 0 ? 0.0 : frame.cascadeSplits[cascade - 1];
                float end = frame.cascadeSplits[cascade];
                float along = (depth - start) / max(end - start, 1e-4);
                if (along > 0.8)
                {
                    float next = cascade + 1 < cascades ? cascadeShadow(cascade + 1, N, L) : 1.0;
                    lit = mix(lit, next, smoothstep(0.8, 1.0, along));
                }

                return lit;
            }

            // ---- Lighting ---------------------------------------------------------------------------------------
            // Physically based: how rough a surface is spreads its highlights and blurs its reflections, metals tint
            // what they reflect, and everything reflects more at grazing angles (Fresnel). Lights here light a white
            // surface facing them fully, with no 1/pi, so the highlights are scaled up to match.

            float distributionGGX(float NdotH, float a)
            {
                float a2 = a * a;
                float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
                return a2 / (PI * d * d);
            }

            float visibilitySmith(float NdotV, float NdotL, float a)
            {
                float a2 = a * a;
                float v = NdotL * sqrt(NdotV * NdotV * (1.0 - a2) + a2);
                float l = NdotV * sqrt(NdotL * NdotL * (1.0 - a2) + a2);
                return 0.5 / max(v + l, 1e-5);
            }

            vec3 fresnel(vec3 F0, float cosine) { return F0 + (1.0 - F0) * pow(1.0 - cosine, 5.0); }

            // How much of the sky a surface reflects overall, by roughness and angle (Karis's fit).
            vec2 environmentBRDF(float roughness, float NdotV)
            {
                const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
                const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
                vec4 r = roughness * c0 + c1;
                float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y;
                return vec2(-1.04, 1.04) * a004 + r.zw;
            }

            // Toon lighting: three flat steps with soft edges between them, rather than a smooth fade.
            float toonBands(float NdotL) { return smoothstep(0.0, 0.08, NdotL) * 0.45 + smoothstep(0.35, 0.45, NdotL) * 0.55; }

            void addLight(vec3 N, vec3 V, vec3 L, vec3 color, vec3 F0, float roughness)
            {
                float NdotL = dot(N, L);
                if (NdotL <= 0.0) return;

                vec3 H = normalize(L + V);
                float NdotV = max(dot(N, V), 1e-4);
                float NdotH = max(dot(N, H), 0.0);
                float a = roughness * roughness;

                vec3 F = fresnel(F0, max(dot(V, H), 0.0));
                float highlight = distributionGGX(NdotH, a) * visibilitySmith(NdotV, NdotL, a) * PI * NdotL;

                // With cel shading, the light falls into bands, and the highlight is a crisp spot instead of a soft one.
                float celShading = frame.horizon.w;
                diffuseLight += color * mix(NdotL, toonBands(NdotL), celShading);
                specularLight += color * F * mix(highlight, smoothstep(0.5, 0.6, highlight) * 2.0, celShading);
            }

            // The surface's normal, bent by the normal map. The directions the map's sideways and up follow on the
            // surface are worked out from how its position and texture coordinates change across the pixel, so meshes
            // don't need tangents (Schüler's method).
            vec3 surfaceNormal(vec2 uv)
            {
                vec3 N = normalize(vNormal);
                if (material.normalParams.x < 0.5) return N;

                vec3 bent = texture(normalMap, uv).xyz * 2.0 - 1.0;
                bent.xy *= material.normalParams.y;

                vec3 dp1 = dFdx(vWorldPosition);
                vec3 dp2 = dFdy(vWorldPosition);
                vec2 duv1 = dFdx(uv);
                vec2 duv2 = dFdy(uv);
                vec3 dp2perp = cross(dp2, N);
                vec3 dp1perp = cross(N, dp1);
                vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
                vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
                float scale = inversesqrt(max(dot(T, T), dot(B, B)));
                if (!(scale < 1e18)) return N;

                return normalize(mat3(T * scale, B * scale, N) * bent);
            }

            void main()
            {
                vec2 uv = vUV * material.uvTransform.xy + material.uvTransform.zw;
                vec4 albedo = material.color;
                if (material.surface.w > 0.5)
                {
                    vec4 texel = texture(materialTexture, uv);
                    albedo *= vec4(toLinear(texel.rgb), texel.a);
                }

                float roughness = clamp(material.surface.x, 0.04, 1.0);
                float metallic = clamp(material.surface.y, 0.0, 1.0);
                vec3 F0 = mix(vec3(0.08 * material.surface.z), albedo.rgb, metallic);
                vec3 diffuseColor = albedo.rgb * (1.0 - metallic);

                vec3 N = surfaceNormal(uv);
                vec3 toCamera = frame.cameraPosition.xyz - vWorldPosition;
                float distance = length(toCamera);
                vec3 V = toCamera / max(distance, 1e-4);
                float NdotV = max(dot(N, V), 1e-4);

                diffuseLight = vec3(0.0);
                specularLight = vec3(0.0);

                int shadowLight = int(frame.shadow.y);
                for (int i = 0; i < frame.lightCounts.x; i++)
                {
                    vec3 L = normalize(-frame.directionalDirection[i].xyz);
                    vec3 color = frame.directionalColor[i].rgb;
                    if (i == shadowLight) color *= sunShadow(normalize(vNormal), L);
                    addLight(N, V, L, color, F0, roughness);
                }

                for (int i = 0; i < frame.lightCounts.y; i++)
                {
                    vec3 toLight = frame.pointPositionRange[i].xyz - vWorldPosition;
                    float lightDistance = length(toLight);
                    float falloff = clamp(1.0 - lightDistance / frame.pointPositionRange[i].w, 0.0, 1.0);
                    addLight(N, V, toLight / max(lightDistance, 1e-4), frame.pointColor[i].rgb * falloff * falloff, F0, roughness);
                }

                // Light from all around, and the sky reflected: sharply in smooth surfaces, blurred to the average
                // light from that side in rough ones. Reflections that would point into the surface fade out.
                float ambientIntensity = frame.skyLight.w;
                vec3 R = reflect(-V, N);
                vec3 reflected = mix(skyRadiance(R), ambientLight(R), smoothstep(0.0, 0.7, roughness));
                reflected *= clamp(1.0 + dot(R, normalize(vNormal)), 0.0, 1.0);
                vec2 brdf = environmentBRDF(roughness, NdotV);
                vec3 ambient = (diffuseColor * ambientLight(N) + reflected * (F0 * brdf.x + brdf.y)) * ambientIntensity;

                vec3 emission = material.emission.rgb;
                vec3 color = diffuseColor * diffuseLight + specularLight + ambient + emission;

                // Fades into the sky with distance. Done last, so far-off things match the sky behind them.
                float fog = fogAmount(distance);
                FragColor = vec4(mix(color, fogColor(-V), fog), 1.0);
                AmbientColor = vec4(ambient * (1.0 - fog), 1.0);
                GlowColor = vec4(emission * GLOW_STRENGTH * (1.0 - fog), 1.0);
            }
            """;

        public const string UnlitFragment = Frame + Material + """
            VARYING(0) in vec3 vWorldPosition;
            VARYING(1) in vec3 vNormal;
            VARYING(2) in vec2 vUV;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 AmbientColor;
            // What glows, in linear light, for bloom: emission, so glowing things keep their colour on screen and still
            // light up the space round them. Nearer things cover glow behind them.
            layout (location = 2) out vec4 GlowColor;

            void main()
            {
                vec2 uv = vUV * material.uvTransform.xy + material.uvTransform.zw;
                vec3 color = material.color.rgb;
                if (material.surface.w > 0.5) color *= toLinear(texture(materialTexture, uv).rgb);
                color += material.emission.rgb;

                vec3 toCamera = frame.cameraPosition.xyz - vWorldPosition;
                float distance = length(toCamera);
                float fog = fogAmount(distance);
                FragColor = vec4(mix(color, fogColor(-toCamera / max(distance, 1e-4)), fog), 1.0);
                AmbientColor = vec4(0.0, 0.0, 0.0, 1.0);
                GlowColor = vec4(material.emission.rgb * GLOW_STRENGTH * (1.0 - fog), 1.0);
            }
            """;

        // Depth only, as seen from the sun. The light-space and model matrices come already multiplied.
        public const string ShadowVertex = """
            #ifdef VERTEX_VULKAN
            layout (push_constant) uniform ShadowDraw { mat4 lightModel; } draw;
            #else
            struct ShadowDraw { mat4 lightModel; };
            uniform ShadowDraw draw;
            #endif

            layout (location = 0) in vec3 aPosition;

            void main()
            {
                gl_Position = draw.lightModel * vec4(aPosition, 1.0);
            }
            """;

        public const string EmptyFragment = """
            void main()
            {
            }
            """;

        // One triangle covering the view, behind everything.
        public const string SkyVertex = """
            // At the far plane (depth 1 in both APIs), so it only fills what nothing else covers.
            VARYING(0) out vec2 vScreen;

            void main()
            {
                vec2 corner = vec2((VERTEX_ID << 1) & 2, VERTEX_ID & 2) * 2.0 - 1.0;
                vScreen = corner;
                gl_Position = vec4(corner, 1.0, 1.0);
            }
            """;

        public const string SkyFragment = Frame + """
            VARYING(0) in vec2 vScreen;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 AmbientColor;
            // What glows, in linear light, for bloom: emission, so glowing things keep their colour on screen and still
            // light up the space round them. Nearer things cover glow behind them.
            layout (location = 2) out vec4 GlowColor;

            // ---- Clouds -----------------------------------------------------------------------------------------
            // A layer of cloud overhead: smooth noise, layered at finer and finer scales, cut off below a level set by
            // how cloudy it is. Thick parts are greyer underneath and edges facing the sun glow.
            //
            // The noise is worked out once and looked up (see CloudNoise.cs), repeating every 64 units.
            TEXTURE(0, 2) sampler2D cloudNoise;

            float noise(vec2 p)
            {
                return textureLod(cloudNoise, p * (1.0 / 64.0), 0.0).r;
            }

            float layered(vec2 p)
            {
                float value = 0.0;
                float amplitude = 0.5;
                for (int i = 0; i < 5; i++)
                {
                    value += amplitude * noise(p);
                    p = p * 2.03 + vec2(17.1, 9.3);
                    amplitude *= 0.5;
                }
                return value;
            }

            vec3 addClouds(vec3 sky, vec3 direction)
            {
                float cover = frame.groundColor.w;
                if (cover <= 0.0 || direction.y <= 0.0) return sky;

                // Where the view meets a flat layer of cloud, drifting slowly with the wind.
                float time = frame.cameraPosition.w;
                vec2 p = direction.xz / (direction.y + 0.1) * 1.4 + vec2(time * 0.006, time * 0.002);
                float threshold = mix(0.78, 0.3, clamp(cover, 0.0, 1.0));
                float density = smoothstep(threshold, threshold + 0.22, layered(p));
                if (density <= 0.0) return sky;

                // Thicker cloud between here and the sun: a second look, a little towards it.
                vec3 sun = frame.sunDirection.xyz;
                float thickness = smoothstep(threshold, threshold + 0.35, layered(p + sun.xz * 0.08));
                vec3 sunLight = frame.sunColor.rgb * sunTransmittance(1.0) * frame.sunDirection.w;
                float towardsSun = pow(max(dot(direction, sun), 0.0), 6.0);
                vec3 light = frame.skyLight.rgb * 1.3 + sunLight * (0.85 - 0.55 * thickness) + sunLight * towardsSun * (1.0 - density) * 0.8;

                // Far off they thin into the haze at the horizon.
                return mix(sky, light, density * smoothstep(0.0, 0.15, direction.y) * 0.95);
            }

            void main()
            {
                vec4 near = frame.inverseViewProjection * vec4(vScreen, 0.0, 1.0);
                vec4 far = frame.inverseViewProjection * vec4(vScreen, 1.0, 1.0);
                vec3 direction = normalize(far.xyz / far.w - near.xyz / near.w);

                vec3 color = skyRadiance(direction);

                // The sun: a bright disc, bright enough to bloom.
                if (frame.sunDirection.w > 0.5 && int(frame.zenith.w + 0.5) > 0)
                {
                    float towardsSun = dot(direction, frame.sunDirection.xyz);
                    float disc = smoothstep(0.99986, 0.99993, towardsSun);
                    vec3 sunLight = frame.sunColor.rgb * (int(frame.zenith.w + 0.5) == 2 ? sunTransmittance(max(direction.y, 0.0)) : vec3(1.0));
                    color += sunLight * disc * 40.0;
                    if (int(frame.zenith.w + 0.5) == 1) color += frame.sunColor.rgb * pow(max(towardsSun, 0.0), 12.0) * 0.18;
                }

                color = addClouds(color, direction);

                FragColor = vec4(color, 1.0);
                AmbientColor = vec4(0.0, 0.0, 0.0, 1.0);
                GlowColor = vec4(0.0, 0.0, 0.0, 1.0);
            }
            """;

        // CPU particles: camera-facing quads built on the CPU, with straight (not premultiplied) sRGB colours.
        public const string ParticleVertex = Frame + """
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec2 aCorner;
            layout (location = 2) in vec4 aColor;
            layout (location = 3) in float aGlow;
            layout (location = 4) in float aAdditive;

            VARYING(0) out vec2 vCorner;
            VARYING(1) out vec4 vColor;
            VARYING(2) out float vGlow;
            VARYING(3) out float vAdditive;
            VARYING(4) out vec3 vToCamera;

            void main()
            {
                vCorner = aCorner;
                vColor = aColor;
                vGlow = aGlow;
                vAdditive = aAdditive;
                vToCamera = frame.cameraPosition.xyz - aPosition;
                gl_Position = frame.projection * frame.view * vec4(aPosition, 1.0);
            }
            """;

        // The GPU particle pool, as the compute shaders and the drawing read it. Matches the layouts in VkGpuParticles.
        public const string ParticleStructs = """
            struct Particle
            {
                vec4 positionAge;
                vec4 velocityLifetime;   // lifetime 0: no particle
                vec4 sizeSystem;         // x: size, y: which system
            };

            struct System
            {
                vec4 startColor;
                vec4 endColor;
                vec4 shape;     // x: end size, y: glow, z: stretch, w: gravity
                vec4 flags;     // x: drag, y: 1 if additive, z: 1 if visible
            };

            """;

        // GPU particles (Vulkan only): each particle's quad made right here from the pool, six corners a particle.
        public const string GpuParticleVertex = Frame + ParticleStructs + """
            layout (set = 1, binding = 0, std430) readonly buffer Particles { Particle particles[]; };
            layout (set = 1, binding = 1, std430) readonly buffer Systems { System systems[]; };

            layout (location = 0) out vec2 vCorner;
            layout (location = 1) out vec4 vColor;
            layout (location = 2) out float vGlow;
            layout (location = 3) out float vAdditive;
            layout (location = 4) out vec3 vToCamera;

            const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));

            void main()
            {
                Particle particle = particles[gl_VertexIndex / 6];
                vec2 corner = corners[gl_VertexIndex % 6];

                // Nothing to draw: put every corner at the same point outside the view, so the quad vanishes.
                gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
                vCorner = corner;
                vColor = vec4(0.0);
                vGlow = 0.0;
                vAdditive = 0.0;
                vToCamera = vec3(0.0, 0.0, 1.0);

                float lifetime = particle.velocityLifetime.w;
                if (lifetime <= 0.0) return;

                System system = systems[uint(particle.sizeSystem.y)];
                if (system.flags.z < 0.5) return;

                float t = particle.positionAge.w / lifetime;
                float size = particle.sizeSystem.x * (1.0 + (system.shape.x - 1.0) * t);
                vec4 color = mix(system.startColor, system.endColor, t);
                if (size <= 0.0 || color.a <= 0.002) return;

                vec3 position = particle.positionAge.xyz;
                vec3 right = vec3(frame.view[0][0], frame.view[1][0], frame.view[2][0]);
                vec3 up = vec3(frame.view[0][1], frame.view[1][1], frame.view[2][1]);
                float halfWidth = size * 0.5;
                float halfLength = halfWidth;

                // Stretched along its movement, turned about that line to face the camera as much as it can.
                vec3 velocity = particle.velocityLifetime.xyz * system.shape.z;
                float stretch = length(velocity);
                if (stretch > 1e-4)
                {
                    vec3 along = velocity / stretch;
                    vec3 side = cross(along, frame.cameraPosition.xyz - position);
                    if (dot(side, side) > 1e-8)
                    {
                        up = along;
                        right = normalize(side);
                        halfLength += stretch * 0.5;
                    }
                }

                vec3 world = position + right * (corner.x * halfWidth) + up * (corner.y * halfLength);
                vColor = color;
                vGlow = system.shape.y;
                vAdditive = system.flags.y;
                vToCamera = frame.cameraPosition.xyz - position;
                gl_Position = frame.projection * frame.view * vec4(world, 1.0);
            }
            """;

        // A soft round dot, blended with premultiplied alpha: an alpha of 0 adds light, anything else covers what's
        // behind. Glowing ones are brighter than white, so they bloom.
        public const string ParticleFragment = Frame + """
            VARYING(0) in vec2 vCorner;
            VARYING(1) in vec4 vColor;
            VARYING(2) in float vGlow;
            VARYING(3) in float vAdditive;
            VARYING(4) in vec3 vToCamera;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 AmbientColor;
            // What glows, in linear light, for bloom: emission, so glowing things keep their colour on screen and still
            // light up the space round them. Nearer things cover glow behind them.
            layout (location = 2) out vec4 GlowColor;

            void main()
            {
                float r2 = dot(vCorner, vCorner);
                if (r2 >= 1.0) discard;

                float shape = (1.0 - r2) * (1.0 - r2);
                float alpha = vColor.a * shape;
                float covers = alpha * (1.0 - vAdditive);

                // In the distance, dust fades into the fog and glowing things fade out.
                float distance = length(vToCamera);
                float fog = fogAmount(distance);
                vec3 linearColor = toLinear(vColor.rgb);
                vec3 color = mix(linearColor, fogColor(-vToCamera / max(distance, 1e-4)), fog * (1.0 - vAdditive)) * mix(1.0, 1.0 - fog, vAdditive);

                FragColor = vec4(color * alpha, covers);
                AmbientColor = vec4(0.0, 0.0, 0.0, covers);
                GlowColor = vec4(linearColor * vGlow * alpha * (1.0 - fog), covers);
            }
            """;

        // ---- The editor's ground grid ------------------------------------------------------------------------------
        // A square on the ground (y = 0) under the camera, far wider than can be seen clearly. Metre lines fade out where
        // they'd crowd together, leaving the ten-metre ones, and everything fades into the distance. The X axis is red
        // and the Z axis blue.
        public const string GridVertex = Frame + """
            VARYING(0) out vec3 vWorld;

            const vec2 CORNERS[6] = vec2[](vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(1.0, 1.0), vec2(-1.0, -1.0), vec2(1.0, 1.0), vec2(-1.0, 1.0));

            void main()
            {
                // Only as big as it can be seen (see the fade below): smaller triangles keep their depth exact.
                vec2 corner = CORNERS[VERTEX_ID] * min(2000.0, 160.0 + abs(frame.cameraPosition.y) * 14.0);
                vWorld = vec3(frame.cameraPosition.x + corner.x, 0.0, frame.cameraPosition.z + corner.y);
                gl_Position = frame.projection * frame.view * vec4(vWorld, 1.0);
            }
            """;

        public const string GridFragment = Frame + """
            VARYING(0) in vec3 vWorld;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 AmbientColor;
            layout (location = 2) out vec4 GlowColor;

            // How much of this pixel a line every `spacing` metres covers: a pixel wide, whatever the distance.
            float lines(vec2 position, float spacing)
            {
                vec2 cell = position / spacing;
                vec2 width = max(fwidth(cell), vec2(1e-5));
                vec2 distanceToLine = abs(fract(cell - 0.5) - 0.5) / width;
                return 1.0 - min(min(distanceToLine.x, distanceToLine.y), 1.0);
            }

            void main()
            {
                vec2 position = vWorld.xz;
                vec2 metresPerPixel = fwidth(position);
                float crowding = max(metresPerPixel.x, metresPerPixel.y);

                float minor = lines(position, 1.0) * (1.0 - smoothstep(0.04, 0.2, crowding));
                float major = lines(position, 10.0) * (1.0 - smoothstep(0.4, 2.0, crowding));
                float alpha = max(minor * 0.22, major * 0.42);

                // The axes, a little bolder.
                float xAxis = 1.0 - min(abs(position.y) / max(metresPerPixel.y * 1.5, 1e-5), 1.0);
                float zAxis = 1.0 - min(abs(position.x) / max(metresPerPixel.x * 1.5, 1e-5), 1.0);
                vec3 color = vec3(0.8);
                color = mix(color, vec3(0.85, 0.16, 0.12), xAxis);
                color = mix(color, vec3(0.14, 0.32, 0.95), zAxis);
                alpha = max(alpha, max(xAxis, zAxis) * 0.75);

                // Fades out with distance, further when the camera is higher up.
                float height = abs(frame.cameraPosition.y);
                float distance = length(frame.cameraPosition.xyz - vWorld);
                alpha *= 1.0 - smoothstep(30.0 + height * 4.0, 120.0 + height * 12.0, distance);

                FragColor = vec4(color * alpha, alpha);
                AmbientColor = vec4(0.0);
                GlowColor = vec4(0.0);

                // Its depth worked out exactly here, and a little nearer than it is (a fifth of a percent of the way to the
                // camera), so it shows on a floor at the same height instead of flickering against it.
                vec4 clip = frame.projection * frame.view * vec4(mix(vWorld, frame.cameraPosition.xyz, 0.002), 1.0);
                gl_FragDepth = STORED_DEPTH(clip.z / clip.w);
            }
            """;

        // ---- Post-processing ----------------------------------------------------------------------------------------

        // One triangle covering the image. vUV (0, 0) is the image's first row in both APIs, so images read here line up
        // with the ones being written. In OpenGL, flipY reads upside down, to put the top row first in a render texture.
        public const string FullscreenVertex = """
            VARYING(0) out vec2 vUV;

            #ifndef VERTEX_VULKAN
            uniform bool flipY;
            #endif

            void main()
            {
                vec2 corner = vec2((VERTEX_ID << 1) & 2, VERTEX_ID & 2) * 2.0 - 1.0;
                vUV = corner * 0.5 + 0.5;
            #ifndef VERTEX_VULKAN
                if (flipY) vUV.y = 1.0 - vUV.y;
            #endif
                gl_Position = vec4(corner, 0.0, 1.0);
            }
            """;

        // What the post passes read, and their settings. Matches PostUniforms.
        public const string Post = """
            TEXTURE(0, 0) sampler2D source;
            TEXTURE(0, 1) sampler2D bloomImage;
            TEXTURE(0, 2) sampler2D depthImage;
            TEXTURE(0, 3) sampler2D occlusionImage;
            TEXTURE(0, 4) sampler2D ambientImage;

            #ifdef VERTEX_VULKAN
            layout (push_constant) uniform PostBlock
            {
            #else
            struct PostBlock
            {
            #endif
                vec2 texelSize;        // one texel of the source
                float bloomStrength;
                float outlines;
                vec4 projection;       // the camera's: M11, M22, M33, M43
                vec2 outlineStep;
                float exposure;
                float vignette;
                float occlusion;       // how strongly ambient occlusion darkens, 0 for none
                float occlusionRadius;
                float threshold;       // bloom: brightness it starts at, on the first step down only
                float time;
                vec2 direction;        // blur: which way
                float pad0;
                float pad1;
            #ifdef VERTEX_VULKAN
            } post;
            #else
            };
            uniform PostBlock post;
            #endif

            VARYING(0) in vec2 vUV;
            layout (location = 0) out vec4 FragColor;

            // How far in front of the camera the scene is here. Depth d is (M33 z + M43) / -z, so z = -M43 / (d + M33).
            float viewDepth(vec2 uv)
            {
                return post.projection.w / (NDC_DEPTH(textureLod(depthImage, uv, 0.0).r) + post.projection.z);
            }

            vec3 viewPosition(vec2 uv)
            {
                float z = viewDepth(uv);
                vec2 ndc = UV_TO_NDC(uv);
                return vec3(ndc.x * z / post.projection.x, ndc.y * z / post.projection.y, -z);
            }

            """;

        // Halves an image, averaging 13 nearby samples (Jimenez's filter from Call of Duty: Advanced Warfare), which blurs
        // it smoothly without flickering as things move. The first step starts the bloom: what glows (the glow image, in
        // bloomImage) and whatever in the scene is brighter than the threshold.
        public const string BloomDownsampleFragment = Post + """
            vec3 tap(sampler2D image, float x, float y) { return texture(image, vUV + vec2(x, y) * post.texelSize).rgb; }

            vec3 halve(sampler2D image)
            {
                vec3 a = tap(image, -2.0, 2.0), b = tap(image, 0.0, 2.0), c = tap(image, 2.0, 2.0);
                vec3 d = tap(image, -2.0, 0.0), e = tap(image, 0.0, 0.0), f = tap(image, 2.0, 0.0);
                vec3 g = tap(image, -2.0, -2.0), h = tap(image, 0.0, -2.0), i = tap(image, 2.0, -2.0);
                vec3 j = tap(image, -1.0, 1.0), k = tap(image, 1.0, 1.0), l = tap(image, -1.0, -1.0), m = tap(image, 1.0, -1.0);
                return max(e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125, vec3(0.0));
            }

            void main()
            {
                vec3 color = halve(source);

                if (post.threshold > 0.0)
                {
                    // At the scene's exposure, eased in around the threshold rather than cut off at it, and a single
                    // very bright pixel can't light up a whole area.
                    color = min(color * post.exposure, vec3(64.0));
                    float brightness = max(color.r, max(color.g, color.b));
                    float knee = post.threshold * 0.5;
                    float soft = clamp(brightness - post.threshold + knee, 0.0, 2.0 * knee);
                    soft = soft * soft / (4.0 * knee + 1e-5);
                    color *= max(soft, brightness - post.threshold) / max(brightness, 1e-5);
                    color += min(halve(bloomImage) * post.exposure, vec3(64.0));
                }

                FragColor = vec4(color, 1.0);
            }
            """;

        // Doubles a blurred image back up with a 3x3 tent filter, added onto the next size up.
        public const string BloomUpsampleFragment = Post + """
            vec3 tap(float x, float y) { return texture(source, vUV + vec2(x, y) * post.texelSize).rgb; }

            void main()
            {
                vec3 color = tap(0.0, 0.0) * 4.0
                    + (tap(-1.0, 0.0) + tap(1.0, 0.0) + tap(0.0, -1.0) + tap(0.0, 1.0)) * 2.0
                    + tap(-1.0, -1.0) + tap(1.0, -1.0) + tap(-1.0, 1.0) + tap(1.0, 1.0);
                FragColor = vec4(color / 16.0, 1.0);
            }
            """;

        // Ambient occlusion: how much of the open space round each point is blocked by what's drawn near it, so corners,
        // creases and the ground under things get less of the soft light from all around. From the depth image alone:
        // points in a half-ball over the surface are checked against the depth there.
        //
        // Worked out at half the view's size (a quarter of the pixels), as it's soft and blurred after anyway. Writes how
        // open each point is in red and how far away it is in green, for the blur and the scaling back up to go by.
        public const string OcclusionFragment = Post + """
            const int SAMPLES = 12;

            void main()
            {
                // From one of the full-size pixels this covers, not blended between them, which at edges would make up a
                // surface between the near one and the far one.
                vec2 texel = 1.0 / vec2(textureSize(depthImage, 0));
                vec2 uv = (floor(vUV / texel - 0.25) + 0.5) * texel;

                float depth = viewDepth(uv);
                if (depth > 150.0) { FragColor = vec4(1.0, min(depth, 60000.0), 0.0, 0.0); return; }

                // The surface's direction, from the nearer of its neighbours on each side, so edges don't smear it.
                vec3 p = viewPosition(uv);
                vec3 right = viewPosition(uv + vec2(texel.x, 0.0)) - p;
                vec3 left = p - viewPosition(uv - vec2(texel.x, 0.0));
                vec3 up = viewPosition(uv + vec2(0.0, texel.y)) - p;
                vec3 down = p - viewPosition(uv - vec2(0.0, texel.y));
                vec3 dx = abs(right.z) < abs(left.z) ? right : left;
                vec3 dy = abs(up.z) < abs(down.z) ? up : down;
                vec3 n = normalize(cross(dx, dy));
                if (dot(n, p) > 0.0) n = -n;

                // Each pixel turns the pattern a different way (interleaved gradient noise), and the blur after evens it out.
                float angle = 6.2831853 * fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
                vec3 random = vec3(cos(angle), sin(angle), 0.0);
                vec3 tangent = normalize(random - n * dot(random, n) + vec3(1e-4, 0.0, 0.0));
                mat3 basis = mat3(tangent, cross(n, tangent), n);

                float radius = post.occlusionRadius;
                float occluded = 0.0;
                for (int i = 0; i < SAMPLES; i++)
                {
                    // Spread round a spiral, more of them close in.
                    float t = (float(i) + 0.5) / float(SAMPLES);
                    float spin = float(i) * 2.399963;
                    float lift = sqrt(1.0 - t);
                    vec3 offset = vec3(cos(spin) * sqrt(1.0 - lift * lift), sin(spin) * sqrt(1.0 - lift * lift), lift) * mix(0.15, 1.0, t * t);
                    vec3 s = p + basis * offset * radius;

                    vec2 sampleUV = NDC_TO_UV(vec2(s.x * post.projection.x, s.y * post.projection.y) / -s.z);
                    float sceneDepth = viewDepth(sampleUV);
                    float inRange = smoothstep(0.0, 1.0, radius / abs(depth - sceneDepth));
                    occluded += (sceneDepth <= -s.z - 0.02 ? 1.0 : 0.0) * inRange;
                }

                float open = 1.0 - occluded / float(SAMPLES);
                FragColor = vec4(mix(open * open, 1.0, smoothstep(100.0, 150.0, depth)), depth, 0.0, 0.0);
            }
            """;

        // Blurs the occlusion one way, only across the same surface (similar depth), so it doesn't bleed past edges. The
        // depth comes with it (see OcclusionFragment), and goes on with it.
        public const string OcclusionBlurFragment = Post + """
            void main()
            {
                float centre = texture(source, vUV).g;
                float sum = 0.0;
                float weights = 0.0;
                for (int i = -4; i <= 4; i++)
                {
                    vec2 s = texture(source, vUV + post.direction * post.texelSize * float(i)).rg;
                    float similar = max(0.0, 1.0 - abs(s.g - centre) / (centre * 0.04 + 0.05));
                    float weight = exp(-float(i * i) / 8.0) * similar;
                    sum += s.r * weight;
                    weights += weight;
                }
                FragColor = vec4(sum / max(weights, 1e-4), centre, 0.0, 0.0);
            }
            """;

        // The finished view: ambient occlusion, outlines, bloom, exposure and tone mapping, then a touch of vignette.
        // Writes how bright each pixel looks into alpha, for the anti-aliasing after.
        public const string CompositeFragment = Post + """
            // One over the distance to what's drawn there; across any flat surface it changes evenly from pixel to pixel.
            float nearness(vec2 uv) { return 1.0 / viewDepth(uv); }

            // How much this pixel is on an edge: where it's nearer or further than the average of the pixels either
            // side, relative to its own nearness, so it's the same near or far.
            float edge()
            {
                float centre = nearness(vUV);
                vec2 x = vec2(post.outlineStep.x, 0.0);
                vec2 y = vec2(0.0, post.outlineStep.y);
                float across = 2.0 * centre - nearness(vUV + x) - nearness(vUV - x);
                float down = 2.0 * centre - nearness(vUV + y) - nearness(vUV - y);
                float strength = max(abs(across), abs(down)) / max(centre, 1e-6);

                float distance = 1.0 / max(centre, 1e-6);
                return smoothstep(0.003, 0.02, strength) * (1.0 - smoothstep(25.0, 80.0, distance));
            }

            // Ambient occlusion is worked out at half size. Each pixel blends the four nearest, as a smooth scaling up
            // would, but favouring those as far away as itself, so it doesn't bleed across the edges of things.
            float occlusionAt(vec2 uv)
            {
                vec2 size = vec2(textureSize(occlusionImage, 0));
                vec2 position = uv * size - 0.5;
                vec2 base = floor(position);
                vec2 f = position - base;
                float depth = viewDepth(uv);

                float sum = 0.0;
                float weights = 0.0;
                for (int i = 0; i < 4; i++)
                {
                    vec2 corner = vec2(float(i & 1), float(i >> 1));
                    vec2 s = texture(occlusionImage, (base + corner + 0.5) / size).rg;
                    vec2 near = mix(1.0 - f, f, corner);
                    float similar = max(1e-3, 1.0 - abs(s.g - depth) / (depth * 0.04 + 0.05));
                    float weight = near.x * near.y * similar;
                    sum += s.r * weight;
                    weights += weight;
                }
                return sum / max(weights, 1e-6);
            }

            // ACES filmic tone mapping (Hill's fit): bright areas roll off smoothly instead of clipping to flat white.
            vec3 toneMap(vec3 color)
            {
                const mat3 toACES = mat3(0.59719, 0.07600, 0.02840, 0.35458, 0.90834, 0.13383, 0.04823, 0.01566, 0.83777);
                const mat3 fromACES = mat3(1.60475, -0.10208, -0.00327, -0.53108, 1.10813, -0.07276, -0.07367, -0.00605, 1.07602);
                vec3 v = toACES * color;
                vec3 a = v * (v + 0.0245786) - 0.000090537;
                vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
                return clamp(fromACES * (a / b), 0.0, 1.0);
            }

            void main()
            {
                vec3 scene = texture(source, vUV).rgb;

                if (post.occlusion > 0.0)
                {
                    float open = mix(1.0, occlusionAt(vUV), post.occlusion);
                    scene = max(scene - texture(ambientImage, vUV).rgb * (1.0 - open), vec3(0.0));
                }

                scene *= post.exposure;
                // Darkened by as much as it looks on screen, not in linear light, where the same cut looks far less.
                if (post.outlines > 0.0) scene *= pow(1.0 - 0.85 * post.outlines * edge(), 2.2);
                if (post.bloomStrength > 0.0) scene += texture(bloomImage, vUV).rgb * post.bloomStrength;

                vec3 color = pow(toneMap(scene), vec3(1.0 / 2.2));

                vec2 fromCentre = (vUV - 0.5) * vec2(1.0, 0.85);
                color *= mix(1.0, smoothstep(0.85, 0.2, length(fromCentre)), post.vignette);

                FragColor = vec4(color, dot(color, vec3(0.299, 0.587, 0.114)));
            }
            """;

        // FXAA 3.11 (Lottes): finds edges by how brightness changes, follows each along to its ends, and blends across
        // it by how far along it the pixel is, smoothing jagged lines.
        public const string AntiAliasFragment = Post + """
            const float QUALITY[12] = float[](1.0, 1.0, 1.0, 1.0, 1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 4.0, 8.0);

            float luma(vec2 uv) { return textureLod(source, uv, 0.0).a; }

            void main()
            {
                vec2 rcp = post.texelSize;
                vec2 uv = vUV;
                vec4 centre = textureLod(source, uv, 0.0);
                float lumaM = centre.a;
                float lumaN = luma(uv + vec2(0.0, -rcp.y));
                float lumaS = luma(uv + vec2(0.0, rcp.y));
                float lumaW = luma(uv + vec2(-rcp.x, 0.0));
                float lumaE = luma(uv + vec2(rcp.x, 0.0));

                float rangeMax = max(max(max(lumaN, lumaS), max(lumaW, lumaE)), lumaM);
                float rangeMin = min(min(min(lumaN, lumaS), min(lumaW, lumaE)), lumaM);
                float range = rangeMax - rangeMin;
                if (range < max(0.0312, rangeMax * 0.125)) { FragColor = vec4(centre.rgb, 1.0); return; }

                float lumaNW = luma(uv + vec2(-rcp.x, -rcp.y));
                float lumaSE = luma(uv + vec2(rcp.x, rcp.y));
                float lumaNE = luma(uv + vec2(rcp.x, -rcp.y));
                float lumaSW = luma(uv + vec2(-rcp.x, rcp.y));

                float lumaNS = lumaN + lumaS;
                float lumaWE = lumaW + lumaE;
                float edgeHorz1 = -2.0 * lumaM + lumaNS;
                float edgeVert1 = -2.0 * lumaM + lumaWE;
                float lumaNESE = lumaNE + lumaSE;
                float lumaNWNE = lumaNW + lumaNE;
                float edgeHorz2 = -2.0 * lumaE + lumaNESE;
                float edgeVert2 = -2.0 * lumaN + lumaNWNE;
                float lumaNWSW = lumaNW + lumaSW;
                float lumaSWSE = lumaSW + lumaSE;
                float edgeHorz4 = abs(edgeHorz1) * 2.0 + abs(edgeHorz2);
                float edgeVert4 = abs(edgeVert1) * 2.0 + abs(edgeVert2);
                float edgeHorz3 = -2.0 * lumaW + lumaNWSW;
                float edgeVert3 = -2.0 * lumaS + lumaSWSE;
                float edgeHorz = abs(edgeHorz3) + edgeHorz4;
                float edgeVert = abs(edgeVert3) + edgeVert4;

                float subpixA = (lumaNS + lumaWE) * 2.0 + lumaNWSW + lumaNESE;
                bool horizontal = edgeHorz >= edgeVert;
                float lengthSign = horizontal ? rcp.y : rcp.x;
                if (!horizontal) { lumaN = lumaW; lumaS = lumaE; }

                float subpixB = subpixA / 12.0 - lumaM;
                float gradientN = lumaN - lumaM;
                float gradientS = lumaS - lumaM;
                float lumaNN = lumaN + lumaM;
                float lumaSS = lumaS + lumaM;
                bool pairN = abs(gradientN) >= abs(gradientS);
                float gradient = max(abs(gradientN), abs(gradientS));
                if (pairN) lengthSign = -lengthSign;
                float subpixC = clamp(abs(subpixB) / range, 0.0, 1.0);

                vec2 posB = uv;
                vec2 stride = horizontal ? vec2(rcp.x, 0.0) : vec2(0.0, rcp.y);
                if (horizontal) posB.y += lengthSign * 0.5; else posB.x += lengthSign * 0.5;

                vec2 posN = posB - stride * QUALITY[0];
                vec2 posP = posB + stride * QUALITY[0];
                float subpixD = -2.0 * subpixC + 3.0;
                float subpixE = subpixC * subpixC;
                if (!pairN) lumaNN = lumaSS;
                float gradientScaled = gradient * 0.25;
                float lumaMM = lumaM - lumaNN * 0.5;
                float subpixF = subpixD * subpixE;
                bool lumaMLTZero = lumaMM < 0.0;

                float lumaEndN = luma(posN) - lumaNN * 0.5;
                float lumaEndP = luma(posP) - lumaNN * 0.5;
                bool doneN = abs(lumaEndN) >= gradientScaled;
                bool doneP = abs(lumaEndP) >= gradientScaled;
                if (!doneN) posN -= stride * QUALITY[1];
                if (!doneP) posP += stride * QUALITY[1];

                for (int i = 2; i < 12 && !(doneN && doneP); i++)
                {
                    if (!doneN) lumaEndN = luma(posN) - lumaNN * 0.5;
                    if (!doneP) lumaEndP = luma(posP) - lumaNN * 0.5;
                    doneN = abs(lumaEndN) >= gradientScaled;
                    doneP = abs(lumaEndP) >= gradientScaled;
                    if (!doneN) posN -= stride * QUALITY[i];
                    if (!doneP) posP += stride * QUALITY[i];
                }

                float dstN = horizontal ? uv.x - posN.x : uv.y - posN.y;
                float dstP = horizontal ? posP.x - uv.x : posP.y - uv.y;
                bool goodSpanN = (lumaEndN < 0.0) != lumaMLTZero;
                bool goodSpanP = (lumaEndP < 0.0) != lumaMLTZero;
                float spanLength = dstP + dstN;
                bool directionN = dstN < dstP;
                float dst = min(dstN, dstP);
                bool goodSpan = directionN ? goodSpanN : goodSpanP;
                float pixelOffset = -dst / spanLength + 0.5;
                float subpixH = subpixF * subpixF * 0.75;
                float offset = max(goodSpan ? pixelOffset : 0.0, subpixH);

                if (horizontal) uv.y += offset * lengthSign; else uv.x += offset * lengthSign;
                FragColor = vec4(textureLod(source, uv, 0.0).rgb, 1.0);
            }
            """;

        // Copies the source as it is: the finished view without anti-aliasing.
        public const string CopyFragment = Post + """
            void main()
            {
                FragColor = vec4(texture(source, vUV).rgb, 1.0);
            }
            """;
    }
}
