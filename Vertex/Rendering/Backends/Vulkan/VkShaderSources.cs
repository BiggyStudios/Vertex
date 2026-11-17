namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The Vulkan backend's shaders, in GLSL 4.50, compiled to SPIR-V when the backend starts (see
    /// <see cref="VkShaderCompiler"/>). They do what the OpenGL backend's do, with Vulkan's conventions: depth is stored as
    /// 0 to 1 as the projection gives it (OpenGL stores 0.5 to 1), and 3D passes and the canvas use a flipped viewport, so
    /// clip space points up as in OpenGL and the first row of every image is its top.
    /// </summary>
    internal static class VkShaderSources
    {
        public const int MaxDirectionalLights = 4;
        public const int MaxPointLights = 8;

        // Everything about the view being drawn, shared by every scene shader. Matches VkFrameData.
        private const string FrameBlock = """
            layout (set = 0, binding = 0, std140) uniform FrameData
            {
                mat4 view;
                mat4 projection;
                mat4 lightSpace;
                mat4 inverseViewProjection;
                vec4 cameraPosition;
                vec4 skyColor;        // w: ambient intensity
                vec4 horizonColor;    // w: fog density
                vec4 groundColor;     // w: exposure
                vec4 fogColor;        // w: cel shading
                vec4 sunDirection;    // towards the sun; w: 1 if there is one
                vec4 sunColor;
                vec4 shadow;          // x: texel size, y: normal offset, z: index of the light casting shadows (-1 for none)
                ivec4 lightCounts;    // x: directional, y: point
                vec4 directionalDirection[4];
                vec4 directionalColor[4];
                vec4 pointPositionRange[8];
                vec4 pointColor[8];
            } frame;

            layout (set = 0, binding = 1) uniform sampler2DShadow shadowMap;
            """;

        // A surface's look. Matches VkMaterialData.
        private const string MaterialBlock = """
            layout (set = 1, binding = 0, std140) uniform MaterialData
            {
                vec4 color;
                vec4 emission;   // w: strength
                vec4 params;     // x: specular, y: shininess, z: 1 if textured
            } material;

            layout (set = 1, binding = 1) uniform sampler2D materialTexture;
            """;

        public const string LitVertex = "#version 450\n" + FrameBlock + """

            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aNormal;
            layout (location = 2) in vec2 aUV;

            layout (push_constant) uniform Draw
            {
                mat4 model;
                mat4 normalMatrix;
            } draw;

            layout (location = 0) out vec3 vWorldPosition;
            layout (location = 1) out vec3 vNormal;
            layout (location = 2) out vec2 vUV;

            void main()
            {
                vec4 worldPosition = draw.model * vec4(aPosition, 1.0);
                vWorldPosition = worldPosition.xyz;
                vNormal = mat3(draw.normalMatrix) * aNormal;
                vUV = aUV;
                gl_Position = frame.projection * frame.view * worldPosition;
            }
            """;

        public const string LitFragment = "#version 450\n" + FrameBlock + MaterialBlock + """

            layout (location = 0) in vec3 vWorldPosition;
            layout (location = 1) in vec3 vNormal;
            layout (location = 2) in vec2 vUV;

            layout (location = 0) out vec4 FragColor;
            // What glows, in linear light, for bloom. Everything drawn into the scene writes it, so nearer things hide glow behind them.
            layout (location = 1) out vec4 GlowColor;

            vec3 diffuseLight;
            vec3 specularLight;

            vec3 toLinear(vec3 color) { return pow(color, vec3(2.2)); }
            vec3 toScreen(vec3 color) { return pow(color, vec3(1.0 / 2.2)); }

            // ACES filmic curve (Narkowicz's fit): bright areas roll off smoothly instead of clipping to flat white.
            vec3 toneMap(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }

            // Toon lighting: three flat steps with soft edges between them, rather than a smooth fade.
            float toonBands(float NdotL) { return smoothstep(0.0, 0.08, NdotL) * 0.45 + smoothstep(0.35, 0.45, NdotL) * 0.55; }

            void addLight(vec3 N, vec3 V, vec3 L, vec3 color)
            {
                float NdotL = max(dot(N, L), 0.0);
                if (NdotL <= 0.0) return;

                float celShading = frame.fogColor.w;
                vec3 H = normalize(L + V);
                float highlight = pow(max(dot(N, H), 0.0), material.params.y);
                diffuseLight += color * mix(NdotL, toonBands(NdotL), celShading);
                specularLight += color * mix(highlight, smoothstep(0.6, 0.7, highlight), celShading) * material.params.x;
            }

            // 1 in full sunlight, 0 in shadow, in between at soft edges.
            float sunShadow(vec3 N, vec3 L)
            {
                float facing = clamp(dot(N, L), 0.0, 1.0);
                vec3 position = vWorldPosition + N * frame.shadow.y * (2.0 - facing);
                vec4 lightSpace = frame.lightSpace * vec4(position, 1.0);
                vec3 p = lightSpace.xyz / lightSpace.w;

                // The shadow map is drawn with the same flipped viewport as everything else, so its top row is +Y.
                vec2 uv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);
                if (uv.x <= 0.0 || uv.x >= 1.0 || uv.y <= 0.0 || uv.y >= 1.0 || p.z >= 1.0) return 1.0;

                // Vulkan stores depth 0 to 1 as the projection gives it.
                float depth = p.z - 0.0004;

                float lit = 0.0;
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        lit += texture(shadowMap, vec3(uv + vec2(x, y) * frame.shadow.x, depth));
                    }
                }
                lit /= 9.0;

                float edge = max(abs(p.x), abs(p.y));
                return mix(lit, 1.0, smoothstep(0.85, 1.0, edge));
            }

            void main()
            {
                vec4 albedo = material.color;
                if (material.params.z > 0.5) albedo *= texture(materialTexture, vUV);
                vec3 baseColor = toLinear(albedo.rgb);

                vec3 N = normalize(vNormal);
                vec3 V = normalize(frame.cameraPosition.xyz - vWorldPosition);

                vec3 skyLight = toLinear(mix(frame.skyColor.rgb, frame.horizonColor.rgb, 0.5));
                diffuseLight = mix(toLinear(frame.groundColor.rgb), skyLight, N.y * 0.5 + 0.5) * frame.skyColor.w;
                specularLight = vec3(0.0);

                int shadowLight = int(frame.shadow.z);
                for (int i = 0; i < frame.lightCounts.x; i++)
                {
                    vec3 L = normalize(-frame.directionalDirection[i].xyz);
                    vec3 color = frame.directionalColor[i].rgb;
                    if (i == shadowLight) color *= sunShadow(N, L);
                    addLight(N, V, L, color);
                }

                for (int i = 0; i < frame.lightCounts.y; i++)
                {
                    vec3 toLight = frame.pointPositionRange[i].xyz - vWorldPosition;
                    float distance = length(toLight);
                    float falloff = clamp(1.0 - distance / frame.pointPositionRange[i].w, 0.0, 1.0);
                    addLight(N, V, toLight / distance, frame.pointColor[i].rgb * falloff * falloff);
                }

                vec3 emission = toLinear(material.emission.rgb) * material.emission.w;
                vec3 color = toScreen(toneMap((baseColor * diffuseLight + specularLight + emission) * frame.groundColor.w));

                float distance = length(frame.cameraPosition.xyz - vWorldPosition);
                float fog = 1.0 - exp(-pow(frame.horizonColor.w * distance, 2.0));
                FragColor = vec4(mix(color, frame.fogColor.rgb, fog), albedo.a);
                GlowColor = vec4(emission * (1.0 - fog), 1.0);
            }
            """;

        public const string UnlitFragment = "#version 450\n" + FrameBlock + MaterialBlock + """

            layout (location = 0) in vec3 vWorldPosition;
            layout (location = 1) in vec3 vNormal;
            layout (location = 2) in vec2 vUV;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 GlowColor;

            void main()
            {
                vec4 color = material.color;
                if (material.params.z > 0.5) color *= texture(materialTexture, vUV);

                float distance = length(frame.cameraPosition.xyz - vWorldPosition);
                float fog = 1.0 - exp(-pow(frame.horizonColor.w * distance, 2.0));
                FragColor = vec4(mix(color.rgb, frame.fogColor.rgb, fog), color.a);
                GlowColor = vec4(pow(material.emission.rgb, vec3(2.2)) * material.emission.w * (1.0 - fog), 1.0);
            }
            """;

        // Depth only, as seen from the sun. The light-space and model matrices come already multiplied.
        public const string ShadowVertex = """
            #version 450

            layout (location = 0) in vec3 aPosition;

            layout (push_constant) uniform Draw
            {
                mat4 lightModel;
            } draw;

            void main()
            {
                gl_Position = draw.lightModel * vec4(aPosition, 1.0);
            }
            """;

        public const string SkyVertex = """
            #version 450

            layout (location = 0) out vec2 vScreen;

            void main()
            {
                vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;
                vScreen = corner;
                gl_Position = vec4(corner, 0.5, 1.0);
            }
            """;

        public const string SkyFragment = "#version 450\n" + FrameBlock + """

            layout (location = 0) in vec2 vScreen;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 GlowColor;

            void main()
            {
                vec4 near = frame.inverseViewProjection * vec4(vScreen, 0.0, 1.0);
                vec4 far = frame.inverseViewProjection * vec4(vScreen, 1.0, 1.0);
                vec3 direction = normalize(far.xyz / far.w - near.xyz / near.w);

                float height = direction.y;
                vec3 color = height >= 0.0
                    ? mix(frame.horizonColor.rgb, frame.skyColor.rgb, pow(height, 0.55))
                    : mix(frame.horizonColor.rgb, mix(frame.horizonColor.rgb, frame.groundColor.rgb, 0.6), clamp(-height * 4.0, 0.0, 1.0));

                vec3 glow = vec3(0.0);
                if (frame.sunDirection.w > 0.5)
                {
                    float towardsSun = max(dot(direction, frame.sunDirection.xyz), 0.0);
                    float disc = pow(towardsSun, 900.0) * 3.0;
                    color += frame.sunColor.rgb * (disc + pow(towardsSun, 12.0) * 0.18);
                    glow = frame.sunColor.rgb * disc * 0.15;
                }

                FragColor = vec4(color, 1.0);
                GlowColor = vec4(glow, 1.0);
            }
            """;

        // CPU particles: camera-facing quads built on the CPU, with straight (not premultiplied) colours.
        public const string ParticleVertex = "#version 450\n" + FrameBlock + """

            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec2 aCorner;
            layout (location = 2) in vec4 aColor;
            layout (location = 3) in float aGlow;
            layout (location = 4) in float aAdditive;

            layout (location = 0) out vec2 vCorner;
            layout (location = 1) out vec4 vColor;
            layout (location = 2) out float vGlow;
            layout (location = 3) out float vAdditive;
            layout (location = 4) out float vFog;

            void main()
            {
                vCorner = aCorner;
                vColor = aColor;
                vGlow = aGlow;
                vAdditive = aAdditive;
                vFog = 1.0 - exp(-pow(frame.horizonColor.w * length(frame.cameraPosition.xyz - aPosition), 2.0));
                gl_Position = frame.projection * frame.view * vec4(aPosition, 1.0);
            }
            """;

        // The GPU particle pool, as the compute shaders and the drawing read it. Matches the layouts in VkGpuParticles.
        private const string ParticleStructs = """
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

        // GPU particles: each particle's quad made right here from the pool, six corners a particle.
        public const string GpuParticleVertex = "#version 450\n" + FrameBlock + ParticleStructs + """

            layout (set = 1, binding = 0, std430) readonly buffer Particles { Particle particles[]; };
            layout (set = 1, binding = 1, std430) readonly buffer Systems { System systems[]; };

            layout (location = 0) out vec2 vCorner;
            layout (location = 1) out vec4 vColor;
            layout (location = 2) out float vGlow;
            layout (location = 3) out float vAdditive;
            layout (location = 4) out float vFog;

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
                vFog = 0.0;

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
                vFog = 1.0 - exp(-pow(frame.horizonColor.w * length(frame.cameraPosition.xyz - position), 2.0));
                gl_Position = frame.projection * frame.view * vec4(world, 1.0);
            }
            """;

        // A soft round dot, blended with premultiplied alpha: an alpha of 0 adds light, anything else covers what's behind.
        public const string ParticleFragment = "#version 450\n" + FrameBlock + """

            layout (location = 0) in vec2 vCorner;
            layout (location = 1) in vec4 vColor;
            layout (location = 2) in float vGlow;
            layout (location = 3) in float vAdditive;
            layout (location = 4) in float vFog;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 GlowColor;

            void main()
            {
                float r2 = dot(vCorner, vCorner);
                if (r2 >= 1.0) discard;

                float shape = (1.0 - r2) * (1.0 - r2);
                float alpha = vColor.a * shape;
                float covers = alpha * (1.0 - vAdditive);

                vec3 color = mix(vColor.rgb, frame.fogColor.rgb, vFog * (1.0 - vAdditive)) * mix(1.0, 1.0 - vFog, vAdditive);
                FragColor = vec4(color * alpha, covers);
                GlowColor = vec4(pow(vColor.rgb, vec3(2.2)) * vGlow * alpha * (1.0 - vFog), covers);
            }
            """;

        // The particle simulation, step for step what CpuParticlePool does.
        private const string ComputeCommon = "#version 450\n" + ParticleStructs + """

            layout (local_size_x = 64) in;

            layout (set = 0, binding = 0, std430) buffer Particles { Particle particles[]; };
            layout (set = 0, binding = 1, std430) readonly buffer Systems { System systems[]; };

            layout (push_constant) uniform Step
            {
                float deltaTime;
                uint count;
                uint emission;
            } step;
            """;

        public const string ParticleUpdateCompute = ComputeCommon + """

            void main()
            {
                uint i = gl_GlobalInvocationID.x;
                if (i >= step.count) return;

                Particle particle = particles[i];
                if (particle.velocityLifetime.w <= 0.0) return;

                particle.positionAge.w += step.deltaTime;
                if (particle.positionAge.w >= particle.velocityLifetime.w)
                {
                    particles[i].velocityLifetime.w = 0.0;
                    return;
                }

                System system = systems[uint(particle.sizeSystem.y)];
                float slow = system.flags.x > 0.0 ? exp(-system.flags.x * step.deltaTime) : 1.0;
                vec3 velocity = (particle.velocityLifetime.xyz + vec3(0.0, -system.shape.w * step.deltaTime, 0.0)) * slow;
                particle.velocityLifetime.xyz = velocity;
                particle.positionAge.xyz += velocity * step.deltaTime;
                particles[i] = particle;
            }
            """;

        public const string ParticleSpawnCompute = ComputeCommon + """

            struct Emission
            {
                mat4 world;
                vec4 axisSpread;      // xyz: direction, w: spread in degrees
                vec4 area;
                vec4 speedLifetime;   // speed min, max, lifetime min, max
                vec4 size;            // min, max
                uvec4 range;          // first slot of the system's share, its size, where in it the first new one goes, how many
                uvec4 misc;           // x: system, y: seed
            };

            layout (set = 0, binding = 2, std430) readonly buffer Emissions { Emission emissions[]; };

            // PCG hash, the same as ParticleRandom on the CPU.
            uint hash(uint value)
            {
                uint state = value * 747796405u + 2891336453u;
                uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
                return (word >> 22u) ^ word;
            }

            float next(inout uint state)
            {
                state = hash(state);
                return float(state >> 8u) / 16777216.0;
            }

            vec3 spreadDirection(vec3 axis, float spread, float random1, float random2)
            {
                float maxAngle = radians(clamp(spread, 0.0, 180.0));
                float cosAngle = 1.0 - random1 * (1.0 - cos(maxAngle));
                float sinAngle = sqrt(max(1.0 - cosAngle * cosAngle, 0.0));
                float around = random2 * 6.28318530718;

                vec3 side = abs(axis.y) < 0.99 ? normalize(cross(axis, vec3(0.0, 1.0, 0.0))) : vec3(1.0, 0.0, 0.0);
                vec3 up = cross(side, axis);
                return axis * cosAngle + (side * cos(around) + up * sin(around)) * sinAngle;
            }

            void main()
            {
                uint i = gl_GlobalInvocationID.x;
                Emission emission = emissions[step.emission];
                if (i >= emission.range.w) return;

                // In the order the CPU draws them: the spot in the area, the direction, then speed, lifetime and size.
                uint state = hash(emission.misc.y ^ (i * 0x9E3779B9u));
                float x = next(state) - 0.5;
                float y = next(state) - 0.5;
                float z = next(state) - 0.5;
                float random1 = next(state);
                float random2 = next(state);
                float speed = mix(emission.speedLifetime.x, emission.speedLifetime.y, next(state));
                float lifetime = mix(emission.speedLifetime.z, emission.speedLifetime.w, next(state));
                float size = mix(emission.size.x, emission.size.y, next(state));

                vec3 direction = spreadDirection(emission.axisSpread.xyz, emission.axisSpread.w, random1, random2);
                vec4 position = emission.world * vec4(vec3(x, y, z) * emission.area.xyz, 1.0);

                uint slot = emission.range.x + (emission.range.z + i) % emission.range.y;
                particles[slot].positionAge = vec4(position.xyz, 0.0);
                particles[slot].velocityLifetime = vec4(direction * speed, lifetime);
                particles[slot].sizeSystem = vec4(size, float(emission.misc.x), 0.0, 0.0);
            }
            """;

        // The 2D layer: positions arrive in pixels from the top-left, colours already multiplied by alpha.
        public const string CanvasVertex = """
            #version 450

            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aUV;
            layout (location = 2) in vec4 aColor;

            layout (push_constant) uniform Screen
            {
                vec2 size;
            } screen;

            layout (location = 0) out vec2 vUV;
            layout (location = 1) out vec4 vColor;

            void main()
            {
                vec2 ndc = vec2(aPosition.x / screen.size.x * 2.0 - 1.0, 1.0 - aPosition.y / screen.size.y * 2.0);
                gl_Position = vec4(ndc, 0.0, 1.0);
                vUV = aUV;
                vColor = aColor;
            }
            """;

        public const string CanvasFragment = """
            #version 450

            layout (location = 0) in vec2 vUV;
            layout (location = 1) in vec4 vColor;

            layout (set = 0, binding = 0) uniform sampler2D canvasTexture;

            layout (location = 0) out vec4 FragColor;

            void main()
            {
                FragColor = texture(canvasTexture, vUV) * vColor;
            }
            """;

        // One triangle covering the image, for the passes that work on whole images. These use an ordinary viewport, so
        // the top row (v = 0) of what they read lands on the top row of what they write.
        public const string FullscreenVertex = """
            #version 450

            layout (location = 0) out vec2 vUV;

            void main()
            {
                vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;
                vUV = corner * 0.5 + 0.5;
                gl_Position = vec4(corner, 0.0, 1.0);
            }
            """;

        private const string PostBlock = """
            layout (set = 0, binding = 0) uniform sampler2D source;
            layout (set = 0, binding = 1) uniform sampler2D bloomImage;
            layout (set = 0, binding = 2) uniform sampler2D depthImage;

            layout (push_constant) uniform Post
            {
                vec2 texelSize;       // bloom passes: one texel of the source
                float bloomStrength;
                float outlines;
                float projectionZ;
                float projectionW;
                vec2 outlineStep;
            } post;

            layout (location = 0) in vec2 vUV;
            layout (location = 0) out vec4 FragColor;
            """;

        public const string BloomDownsampleFragment = "#version 450\n" + PostBlock + """

            vec3 tap(float x, float y) { return texture(source, vUV + vec2(x, y) * post.texelSize).rgb; }

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

        public const string BloomUpsampleFragment = "#version 450\n" + PostBlock + """

            vec3 tap(float x, float y) { return texture(source, vUV + vec2(x, y) * post.texelSize).rgb; }

            void main()
            {
                vec3 color = tap(0.0, 0.0) * 4.0
                    + (tap(-1.0, 0.0) + tap(1.0, 0.0) + tap(0.0, -1.0) + tap(0.0, 1.0)) * 2.0
                    + tap(-1.0, -1.0) + tap(1.0, -1.0) + tap(-1.0, 1.0) + tap(1.0, 1.0);
                FragColor = vec4(color / 16.0, 1.0);
            }
            """;

        // The finished scene with outlines and bloom, as the OpenGL backend's composite.
        public const string CompositeFragment = "#version 450\n" + PostBlock + """

            // One over the distance to what's drawn there. Vulkan stores the projection's depth d as it is, which for the
            // projection is M43 / distance - M33; so 1 / distance is (d + M33) / M43.
            float nearness(vec2 uv) { return (texture(depthImage, uv).r + post.projectionZ) / post.projectionW; }

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

            void main()
            {
                vec3 scene = pow(texture(source, vUV).rgb, vec3(2.2));
                if (post.outlines > 0.0) scene *= pow(1.0 - 0.85 * post.outlines * edge(), 2.2);
                vec3 bloom = 1.0 - exp(-texture(bloomImage, vUV).rgb * post.bloomStrength);
                vec3 color = 1.0 - (1.0 - scene) * (1.0 - bloom);
                FragColor = vec4(pow(color, vec3(1.0 / 2.2)), 1.0);
            }
            """;
    }
}
