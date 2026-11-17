using Vertex.Rendering.Shaders;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The shaders only the Vulkan backend has: the GPU particle simulation (compute shaders, which OpenGL 3.3 lacks) and
    /// the 2D canvas. The scene and post-processing shaders are shared with OpenGL; see <see cref="ShaderLibrary"/>.
    /// </summary>
    internal static class VkShaderSources
    {
        // The particle simulation, step for step what CpuParticlePool does.
        private const string ComputeCommon = "#version 450\n" + ShaderLibrary.ParticleStructs + """

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

        // The 2D layer: positions arrive in pixels from the top-left, colours already multiplied by alpha. Each quad can
        // be cut to a smooth-edged rounded rectangle and clipped to a rectangle (see CanvasQuad).
        public const string CanvasVertex = """
            #version 450

            layout (location = 0) in vec2 aPosition;
            layout (location = 1) in vec2 aUV;
            layout (location = 2) in vec4 aColor;
            layout (location = 3) in vec4 aShape;
            layout (location = 4) in vec4 aShapeStyle;
            layout (location = 5) in vec4 aClip;

            layout (push_constant) uniform Screen
            {
                vec2 size;
            } screen;

            layout (location = 0) out vec2 vUV;
            layout (location = 1) out vec4 vColor;
            layout (location = 2) out vec2 vPixel;
            layout (location = 3) flat out vec4 vShape;
            layout (location = 4) flat out vec4 vShapeStyle;
            layout (location = 5) flat out vec4 vClip;

            void main()
            {
                vec2 ndc = vec2(aPosition.x / screen.size.x * 2.0 - 1.0, 1.0 - aPosition.y / screen.size.y * 2.0);
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
            #version 450

            layout (location = 0) in vec2 vUV;
            layout (location = 1) in vec4 vColor;
            layout (location = 2) in vec2 vPixel;
            layout (location = 3) flat in vec4 vShape;
            layout (location = 4) flat in vec4 vShapeStyle;
            layout (location = 5) flat in vec4 vClip;

            layout (set = 0, binding = 0) uniform sampler2D canvasTexture;

            layout (location = 0) out vec4 FragColor;

            """ + ShaderLibrary.CanvasShape + """

            void main()
            {
                FragColor = texture(canvasTexture, vUV) * vColor * canvasCoverage(vPixel, vShape, vShapeStyle, vClip);
            }
            """;

    }
}
