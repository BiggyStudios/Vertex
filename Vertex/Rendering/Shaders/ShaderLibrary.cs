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
        public const int MaxPointLights = 256;
        public const int MaxCascades = 4;
        public const int MaxFogVolumes = 8;

        /// <summary>How many slices deep the volumetric fog is worked out in (see <see cref="FogLightFragment"/>).</summary>
        public const int FogSlices = 64;

        /// <summary>How many of the fog's slices each fog pass draws at once, one into each of its outputs.</summary>
        public const int FogSlicesPerPass = 8;

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

            // Signed distance to a triangle: negative inside.
            float triangleDistance(vec2 p, vec2 p0, vec2 p1, vec2 p2)
            {
                vec2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
                vec2 v0 = p - p0, v1 = p - p1, v2 = p - p2;
                vec2 pq0 = v0 - e0 * clamp(dot(v0, e0) / dot(e0, e0), 0.0, 1.0);
                vec2 pq1 = v1 - e1 * clamp(dot(v1, e1) / dot(e1, e1), 0.0, 1.0);
                vec2 pq2 = v2 - e2 * clamp(dot(v2, e2) / dot(e2, e2), 0.0, 1.0);
                float s = sign(e0.x * e2.y - e0.y * e2.x);
                vec2 d = min(min(vec2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                                 vec2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
                                 vec2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
                return -sqrt(d.x) * sign(d.y);
            }

            float canvasCoverage(vec2 pixel, vec4 shape, vec4 style, vec4 clip)
            {
                if (clip.z > clip.x && (pixel.x < clip.x || pixel.y < clip.y || pixel.x >= clip.z || pixel.y >= clip.w)) return 0.0;

                // A line with round ends from shape.xy to shape.zw, style.x either side of it.
                if (style.w > 0.5 && style.w < 1.5)
                {
                    vec2 along = shape.zw - shape.xy;
                    float h = clamp(dot(pixel - shape.xy, along) / max(dot(along, along), 1e-6), 0.0, 1.0);
                    return 1.0 - smoothstep(-0.5, 0.5, length(pixel - shape.xy - along * h) - style.x);
                }

                // A triangle with corners shape.xy, shape.zw and style.xy.
                if (style.w > 1.5) return 1.0 - smoothstep(-0.5, 0.5, triangleDistance(pixel, shape.xy, shape.zw, style.xy));

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
                vec4 shadow;            // x: 1 / shadow map size, y: index of the light casting shadows (-1 for none), z: cascades; w: 1 if the editor grid stands in the X-Y plane (2D)
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
                ivec4 lightCounts;      // x: directional, y: point and spot, z: fog volumes
                vec4 directionalDirection[4];
                vec4 directionalColor[4];   // w: how brightly it shows in volumetric fog
                vec4 pointPositionRange[256];
                vec4 pointColor[256];   // w: how brightly it shows in volumetric fog
                vec4 pointSpot[256];    // a spot light's cone: see pointLight
                mat4 previousViewProjection;   // last frame's, for the fog to find where it was
                vec4 fogShape;          // x: density, y: the height it thins above, z: how quickly, w: how patchy
                vec4 fogColor;          // linear; w: forward scattering
                vec4 fogReach;          // x: how far it's worked out, y: how much the sky lights it, z: 1 if on, w: how much of last frame's to keep
                vec4 fogWind;           // xyz: how far the wind has carried it, w: which frame (for each to sample differently)
                vec4 fogGrid;           // xyz: how many cells across, down and deep it's worked out in
                mat4 fogBoxes[8];       // into each fog volume's box
                vec4 fogBoxParams[8];   // x: its density, y: how soft its sides are
                vec4 retro;             // x: rows of the screen grid corners snap to (0 for none), y: how much textures bend (affine), z: 1 for crisp texels
                vec4 clusters;          // x, y: a point's light cluster slice is log(depth) * x + y (see lightCluster)
                mat4 localShadowMatrix[16];   // into each spot light's shadow map (see LocalShadows)
                vec4 localShadowParams[16];   // x: its first layer, y: a texel's size a unit from the light, z: near, w: far (negative for a point light)
                uvec4 lightShadows[16];       // which of those each point and spot light has, plus one (0 for none): a byte each, four to a word
            } frame;

            TEXTURE(0, 1) sampler2DArrayShadow shadowMap;

            // The volumetric fog between the camera and each slice of the view (see FogTotalFragment).
            TEXTURE(0, 3) sampler2DArray fogVolume;

            // Which point and spot lights reach each part of the view (see LightClusters): a word for each cluster, then
            // the lists of lights, four to a word.
            #ifdef VERTEX_VULKAN
            layout (set = 0, binding = 4, std430) readonly buffer LightClusterData { uint lightClusterWords[]; };
            #define CLUSTER_WORD(i) lightClusterWords[i]
            #else
            uniform usamplerBuffer lightClusterWords;
            #define CLUSTER_WORD(i) texelFetch(lightClusterWords, i).r
            #endif

            // The scene's baked light (see BakedLighting): RGBM, a square root of light over LIGHTMAP_RANGE.
            TEXTURE(0, 5) sampler2D lightmap;
            const float LIGHTMAP_RANGE = 16.0;

            vec3 bakedLightAt(vec2 at)
            {
                vec4 texel = textureLod(lightmap, at, 0.0);
                vec3 root = texel.rgb * texel.a;
                return root * root * LIGHTMAP_RANGE;
            }

            const int CLUSTER_TILES_X = 16;
            const int CLUSTER_TILES_Y = 9;
            const int CLUSTER_SLICES = 24;
            const int CLUSTER_COUNT = CLUSTER_TILES_X * CLUSTER_TILES_Y * CLUSTER_SLICES;

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

            // How much of point or spot light i reaches a point, in its colour, and which way it comes from (L). It fades
            // out over its range; a spot light's cone too, from full inside its soft edge to nothing outside it. The cone
            // is packed as the way it shines times 1 / (cos inner - cos outer), and -cos outer times that, so it's one
            // dot product here; a point light has 0, 0, 0, 1, which is full every way.
            vec3 pointLight(int i, vec3 position, out vec3 L)
            {
                vec3 toLight = frame.pointPositionRange[i].xyz - position;
                float range = abs(frame.pointPositionRange[i].w);   // negative for a baked light, which only lights fog
                float distanceSquared = dot(toLight, toLight);
                L = toLight * inversesqrt(max(distanceSquared, 1e-8));
                if (distanceSquared >= range * range) return vec3(0.0);

                float falloff = 1.0 - sqrt(distanceSquared) / range;
                vec4 spot = frame.pointSpot[i];
                float cone = clamp(dot(-L, spot.xyz) + spot.w, 0.0, 1.0);
                return frame.pointColor[i].rgb * (falloff * falloff * cone * cone);
            }

            // The point and spot lights that can reach a point: where its cluster's list starts (x) and how many are in it
            // (y). The cluster is the tile of the view it's seen in, and how far in front of the camera it is.
            ivec2 lightCluster(vec3 position)
            {
                if (frame.lightCounts.y == 0) return ivec2(0);
                vec4 clip = frame.projection * frame.view * vec4(position, 1.0);
                vec2 ndc = clip.xy / clip.w;
                float depth = dot(position - frame.cameraPosition.xyz, frame.cameraForward.xyz);
                int x = clamp(int((ndc.x * 0.5 + 0.5) * float(CLUSTER_TILES_X)), 0, CLUSTER_TILES_X - 1);
                int y = clamp(int((ndc.y * 0.5 + 0.5) * float(CLUSTER_TILES_Y)), 0, CLUSTER_TILES_Y - 1);
                int z = depth <= 0.5 ? 0 : clamp(int(log(depth) * frame.clusters.x + frame.clusters.y), 0, CLUSTER_SLICES - 1);
                uint word = CLUSTER_WORD((z * CLUSTER_TILES_Y + y) * CLUSTER_TILES_X + x);
                return ivec2(int(word >> 9u), int(word & 511u));
            }

            // Which light is at place `entry` in the lists.
            int clusterLight(int entry)
            {
                uint word = CLUSTER_WORD(CLUSTER_COUNT + (entry >> 2));
                return int((word >> uint((entry & 3) * 8)) & 255u);
            }

            // ---- Point and spot lights' shadows (see LocalShadows) ----------------------------------------------
            // Each in layers of one map that's kept from frame to frame: a spot light's in one, a point light's in six,
            // one for each way it shines (+x, -x, +y, -y, +z, -z), each looking a little more than a quarter turn wide so
            // lookups near their edges stay on them.
            TEXTURE(0, 6) sampler2DArrayShadow localShadowMap;
            const float LOCAL_SHADOW_SIZE = 512.0;
            const float LOCAL_FACE_SCALE = 0.99225;
            const vec3 LOCAL_FACE_FORWARD[6] = vec3[6](vec3(1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, -1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, -1.0));
            const vec3 LOCAL_FACE_UP[6] = vec3[6](vec3(0.0, 1.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 1.0, 0.0));

            // Which of the view's shadows light i has, or -1.
            int lightShadow(int light)
            {
                uint word = frame.lightShadows[light >> 4][(light >> 2) & 3];
                return int((word >> uint((light & 3) * 8)) & 255u) - 1;
            }

            // How much of a light reaches a point past what's in its way, from 1 (all) to 0 (in shadow): softened over a
            // few lookups for surfaces, one for fog.
            float localShadow(int slot, vec3 lightPosition, vec3 position, vec3 normal, bool soft)
            {
                vec4 params = frame.localShadowParams[slot];
                vec3 fromLight = position - lightPosition;
                float distance = length(fromLight);

                // Moved off the surface by about a texel there, so it doesn't shadow itself: further where it turns away
                // from the light. Only the backs of things are in the shadow map, so lit surfaces need little; any more,
                // and where something sits on the floor, the floor right beside it looks up from inside it, and is lit.
                float texel = params.y * distance;
                vec3 toLight = -fromLight / max(distance, 1e-4);
                float facing = clamp(dot(normal, toLight), 0.0, 1.0);
                float away = 1.0 - facing;
                vec3 p = position + normal * (texel * (0.5 + 4.0 * away * away));
                fromLight = p - lightPosition;

                float layer = params.x;
                vec3 ndc;
                if (params.w < 0.0)
                {
                    float near = params.z, far = -params.w;
                    vec3 a = abs(fromLight);
                    int face = a.x >= a.y && a.x >= a.z ? (fromLight.x > 0.0 ? 0 : 1) : a.y >= a.z ? (fromLight.y > 0.0 ? 2 : 3) : (fromLight.z > 0.0 ? 4 : 5);
                    vec3 forward = LOCAL_FACE_FORWARD[face];
                    vec3 across = normalize(cross(LOCAL_FACE_UP[face], -forward));
                    vec3 above = cross(-forward, across);
                    float d = max(dot(fromLight, forward), 1e-4);
                    ndc = vec3(vec2(dot(fromLight, across), dot(fromLight, above)) * (LOCAL_FACE_SCALE / d), far * (d - near) / ((far - near) * d));
                    layer += float(face);
                }
                else
                {
                    vec4 clip = frame.localShadowMatrix[slot] * vec4(p, 1.0);
                    if (clip.w <= 0.0) return 1.0;
                    ndc = clip.xyz / clip.w;
                }

                if (ndc.z >= 1.0) return 1.0;
                vec2 uv = NDC_TO_UV(ndc.xy);
                float depth = STORED_DEPTH(ndc.z) - 0.00004;
                if (!soft) return texture(localShadowMap, vec4(uv, layer, depth));

                float o = 0.8 / LOCAL_SHADOW_SIZE;
                return 0.25 * (texture(localShadowMap, vec4(uv + vec2(-o, -o), layer, depth)) + texture(localShadowMap, vec4(uv + vec2(o, -o), layer, depth)) +
                               texture(localShadowMap, vec4(uv + vec2(-o, o), layer, depth)) + texture(localShadowMap, vec4(uv + vec2(o, o), layer, depth)));
            }

            // ---- Volumetric fog ---------------------------------------------------------------------------------
            // What's between the camera and a point `depth` in front of it, seen through `uv` (0 to 1 across the view,
            // as images are read): the light the fog adds there (rgb), and how much of what's behind still shows (a).
            // Worked out over the view in slices, closer together near the camera (see FogTotalFragment); layer k
            // holds the fog up to the far side of slice k.
            vec4 fogBetween(vec2 uv, float depth)
            {
                if (frame.fogReach.z < 0.5) return vec4(0.0, 0.0, 0.0, 1.0);

                float slices = frame.fogGrid.z;
                float layer = sqrt(clamp(depth / frame.fogReach.x, 0.0, 1.0)) * slices - 1.0;
                float below = floor(layer);
                vec4 near = below < 0.0 ? vec4(0.0, 0.0, 0.0, 1.0) : texture(fogVolume, vec3(uv, below));
                vec4 far = texture(fogVolume, vec3(uv, min(below + 1.0, slices - 1.0)));
                return mix(near, far, layer - below);
            }

            vec4 fogAtPoint(vec3 position)
            {
                if (frame.fogReach.z < 0.5) return vec4(0.0, 0.0, 0.0, 1.0);

                // With perspective, w is how far in front of the camera it is.
                vec4 clip = frame.projection * frame.view * vec4(position, 1.0);
                return fogBetween(NDC_TO_UV(clip.xy / clip.w), clip.w);
            }

            """;

        // ---- Volumetric fog ------------------------------------------------------------------------------------------
        // Worked out before the scene is drawn, in a grid of cells over the view (a few pixels across, and slices deep,
        // closer together near the camera), each a layer of an image array. Two passes, each drawing several slices at
        // once into its outputs: FogLightFragment works out how thick the fog is in each cell and how much light it
        // sends towards the camera; FogTotalFragment adds them up from the camera outwards. The scene's shaders then
        // look up what's in front of each thing they draw (fogBetween).

        // Which slices a fog pass draws: push constants in Vulkan, a uniform struct in OpenGL.
        public const string FogPass = """
            #ifdef VERTEX_VULKAN
            layout (push_constant) uniform FogPassBlock { vec4 settings; } fogPass;
            #else
            struct FogPassBlock { vec4 settings; };
            uniform FogPassBlock fogPass;
            #endif

            // How far in front of the camera slice boundary t is (t from 0 to the number of slices).
            float sliceDepth(float t)
            {
                float s = t / frame.fogGrid.z;
                return frame.fogReach.x * s * s;
            }

            // Which way the view looks through uv (0 to 1 across it, as images are read).
            vec3 rayThrough(vec2 uv)
            {
                vec2 ndc = UV_TO_NDC(uv);
                vec4 near = frame.inverseViewProjection * vec4(ndc, 0.0, 1.0);
                vec4 far = frame.inverseViewProjection * vec4(ndc, 1.0, 1.0);
                return normalize(far.xyz / far.w - near.xyz / near.w);
            }

            VARYING(0) in vec2 vUV;
            layout (location = 0) out vec4 Slice0;
            layout (location = 1) out vec4 Slice1;
            layout (location = 2) out vec4 Slice2;
            layout (location = 3) out vec4 Slice3;
            layout (location = 4) out vec4 Slice4;
            layout (location = 5) out vec4 Slice5;
            layout (location = 6) out vec4 Slice6;
            layout (location = 7) out vec4 Slice7;

            void writeSlices(vec4 results[8])
            {
                Slice0 = results[0]; Slice1 = results[1]; Slice2 = results[2]; Slice3 = results[3];
                Slice4 = results[4]; Slice5 = results[5]; Slice6 = results[6]; Slice7 = results[7];
            }

            """;

        // How thick the fog is in each cell and how much light it sends towards the camera (rgb: that light times the
        // density; a: the density). Each cell samples a different point within itself every frame, and is blended with
        // what it was last frame (found where it was in last frame's view), so over a few frames each cell becomes the
        // average over all of it: smooth, without needing many cells.
        public const string FogLightFragment = Frame + FogPass + """
            TEXTURE(0, 2) sampler2D cloudNoise;
            TEXTURE(1, 0) sampler2DArray fogHistory;

            // Smooth noise in 3D from the 2D cloud noise: layers a unit apart, each shifted by a different amount,
            // blended between.
            float fogNoise(vec3 p)
            {
                float layer = floor(p.y);
                float f = p.y - layer;
                f = f * f * (3.0 - 2.0 * f);
                vec2 a = p.xz + vec2(37.0, 17.0) * layer;
                return mix(textureLod(cloudNoise, a / 64.0, 0.0).r, textureLod(cloudNoise, (a + vec2(37.0, 17.0)) / 64.0, 0.0).r, f);
            }

            float fogDensity(vec3 p)
            {
                // Full up to its height, thinning above it.
                float density = frame.fogShape.x * exp(-max(p.y - frame.fogShape.y, 0.0) * frame.fogShape.z);

                // Fog volumes, thicker or thinner inside, fading in from their sides.
                for (int i = 0; i < frame.lightCounts.z; i++)
                {
                    vec3 local = (frame.fogBoxes[i] * vec4(p, 1.0)).xyz;
                    vec3 inside = 0.5 - abs(local);
                    float depthInside = min(inside.x, min(inside.y, inside.z));
                    if (depthInside <= 0.0) continue;
                    density += frame.fogBoxParams[i].x * smoothstep(0.0, max(frame.fogBoxParams[i].y * 0.5, 1e-3), depthInside);
                }

                // Thicker and thinner patches, drifting with the wind; at full noise, gaps of clear air between. The
                // volumes' fog too, so their edges are broken up rather than straight.
                float patches = 1.0;
                if (frame.fogShape.w > 0.0)
                {
                    vec3 q = (p - frame.fogWind.xyz) * 0.09;
                    float n = fogNoise(q) * 0.65 + fogNoise(q * 2.7 + vec3(11.0, 5.0, 3.0)) * 0.35;
                    patches = mix(1.0, smoothstep(0.2, 0.8, n) * 2.0, frame.fogShape.w);
                }

                return max(density, 0.0) * patches;
            }

            // How much light carries on at an angle (cosine) from the way it was going (Henyey-Greenstein), times pi to
            // match the scene's lights, which light a white surface facing them fully.
            float scattering(float cosine)
            {
                float g = frame.fogColor.w;
                float g2 = g * g;
                return 0.25 * (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * cosine, 1e-4), 1.5);
            }

            // How much sun reaches a point in the fog: one lookup in its cascade, so its shadows fall through it.
            float sunAt(vec3 p)
            {
                int cascades = int(frame.shadow.z);
                float depth = dot(p - frame.cameraPosition.xyz, frame.cameraForward.xyz);
                for (int i = 0; i < cascades; i++)
                {
                    if (depth >= frame.cascadeSplits[i]) continue;

                    vec4 lightSpace = frame.shadowMatrices[i] * vec4(p, 1.0);
                    vec3 s = lightSpace.xyz / lightSpace.w;
                    vec2 uv = NDC_TO_UV(s.xy);
                    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || s.z >= 1.0) return 1.0;
                    return texture(shadowMap, vec4(uv, float(i), STORED_DEPTH(s.z) - 0.0005));
                }
                return 1.0;
            }

            vec3 lightAt(vec3 p, vec3 view)
            {
                vec3 light = frame.skyLight.rgb * frame.skyLight.w * frame.fogReach.y;

                int shadowLight = int(frame.shadow.y);
                for (int i = 0; i < frame.lightCounts.x; i++)
                {
                    vec3 L = normalize(-frame.directionalDirection[i].xyz);
                    vec3 color = frame.directionalColor[i].rgb * (frame.directionalColor[i].w * scattering(dot(L, view)));
                    if (i == shadowLight) color *= sunAt(p);
                    light += color;
                }

                ivec2 cluster = lightCluster(p);
                for (int k = 0; k < cluster.y; k++)
                {
                    int i = clusterLight(cluster.x + k);
                    if (frame.pointColor[i].w <= 0.0) continue;
                    vec3 L;
                    vec3 color = pointLight(i, p, L);
                    int shadow = lightShadow(i);
                    if (shadow >= 0 && color.r + color.g + color.b > 0.0) color *= localShadow(shadow, frame.pointPositionRange[i].xyz, p, vec3(0.0), false);
                    light += color * (frame.pointColor[i].w * scattering(dot(L, view)));
                }

                return light;
            }

            void main()
            {
                vec2 grid = frame.fogGrid.xy;
                float slices = frame.fogGrid.z;
                vec2 cell = floor(gl_FragCoord.xy);
                vec3 eye = frame.cameraPosition.xyz;
                vec3 forward = frame.cameraForward.xyz;

                // Where in the cell to sample this frame: a different spot each frame (an R3 sequence), and each cell
                // starting at a different place in it (interleaved gradient noise), so neighbours don't move together.
                float start = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
                vec3 jitter = fract(vec3(0.8191725, 0.6710436, 0.5497005) * frame.fogWind.w + start);

                vec3 view = rayThrough((cell + jitter.xy) / grid);
                float along = 1.0 / max(dot(view, forward), 1e-4);
                vec3 middle = rayThrough((cell + 0.5) / grid);
                float middleAlong = 1.0 / max(dot(middle, forward), 1e-4);

                vec4 results[8];
                for (int s = 0; s < 8; s++)
                {
                    float slice = fogPass.settings.x + float(s);
                    vec3 p = eye + view * (sliceDepth(slice + jitter.z) * along);
                    float density = fogDensity(p);
                    vec3 light = density > 0.0 ? lightAt(p, view) : vec3(0.0);
                    vec4 cellNow = vec4(light * frame.fogColor.rgb * density, density);

                    // What this cell's middle was last frame, where it was then: kept mostly, so the samples add up.
                    float keep = frame.fogReach.w;
                    if (keep > 0.0)
                    {
                        vec4 before = frame.previousViewProjection * vec4(eye + middle * (sliceDepth(slice + 0.5) * middleAlong), 1.0);
                        if (before.w > 1e-4)
                        {
                            vec2 uv = NDC_TO_UV(before.xy / before.w);
                            float layer = sqrt(clamp(before.w / frame.fogReach.x, 0.0, 1.0)) * slices - 0.5;
                            if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0))) && layer <= slices - 0.5)
                            {
                                layer = max(layer, 0.0);
                                float below = floor(layer);
                                vec4 then = mix(texture(fogHistory, vec3(uv, below)),
                                                texture(fogHistory, vec3(uv, min(below + 1.0, slices - 1.0))), layer - below);
                                cellNow = mix(cellNow, then, keep);
                            }
                        }
                    }

                    results[s] = cellNow;
                }

                writeSlices(results);
            }
            """;

        // The fog from the camera out to the far side of each slice: the light it adds (rgb), and how much of what's
        // behind still shows through (a). Each pass adds up every slice from the camera to its last.
        public const string FogTotalFragment = Frame + FogPass + """
            TEXTURE(1, 0) sampler2DArray fogLight;

            void main()
            {
                ivec2 cell = ivec2(gl_FragCoord.xy);
                // How much longer the view's path is through each slice than the slice is deep, looking this way.
                float along = 1.0 / max(dot(rayThrough((vec2(cell) + 0.5) / frame.fogGrid.xy), frame.cameraForward.xyz), 1e-4);

                int first = int(fogPass.settings.x + 0.5);
                vec3 light = vec3(0.0);
                float through = 1.0;
                vec4 results[8];
                for (int k = 0; k < first + 8; k++)
                {
                    vec4 s = texelFetch(fogLight, ivec3(cell, k), 0);
                    float extinction = max(s.a, 1e-6);
                    float span = (sliceDepth(float(k + 1)) - sliceDepth(float(k))) * along;
                    float kept = exp(-extinction * span);

                    // The light the slice sends on, less what the slice itself takes out of it on the way (Hillaire's).
                    light += through * s.rgb * (1.0 - kept) / extinction;
                    through *= kept;
                    if (k >= first) results[k - first] = vec4(light, through);
                }

                writeSlices(results);
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
                vec4 values[16];     // a surface shader's settings (see Shader.PackValues)
            } material;

            TEXTURE(1, 1) sampler2D materialTexture;
            TEXTURE(1, 2) sampler2D normalMap;

            // Where on the material's texture this is: in perspective, or bent across the surface as early consoles drew
            // textures (see Lighting.AffineTextures), as much as the scene asks.
            vec2 materialUV(vec2 uv, vec2 affineUV)
            {
                return mix(uv, affineUV, frame.retro.y) * material.uvTransform.xy + material.uvTransform.zw;
            }

            // A texture, smoothed, or in crisp square texels with no smaller copies (Lighting.PixelatedTextures).
            vec4 crispTexture(sampler2D image, vec2 uv)
            {
                if (frame.retro.z < 0.5) return texture(image, uv);
                vec2 size = vec2(textureSize(image, 0));
                return textureLod(image, (floor(uv * size) + 0.5) / size, 0.0);
            }

            vec4 materialTexel(vec2 uv) { return crispTexture(materialTexture, uv); }

            """;

        private const string DrawConstants = """
            // Each draw's: where it is, and its baked light (lightmap.x is 1 for the lightmap, lightmap.y 1 for probes;
            // probe is the probes' light round it, for red, green and blue: see ProbeLight).
            #ifdef VERTEX_VULKAN
            layout (push_constant) uniform DrawBlock { mat4 model; vec4 lightmap; vec4 probe[3]; } draw;
            #else
            struct DrawBlock { mat4 model; vec4 lightmap; vec4 probe[3]; };
            uniform DrawBlock draw;
            #endif

            """;

        public const string MeshVertex = Frame + DrawConstants + """
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec3 aNormal;
            layout (location = 2) in vec2 aUV;
            layout (location = 3) in vec2 aLightmapUV;

            VARYING(0) out vec3 vWorldPosition;
            VARYING(1) out vec3 vNormal;
            VARYING(2) out vec2 vUV;
            // The same, spread evenly across the screen rather than in perspective: how early consoles drew textures.
            VARYING(3) noperspective out vec2 vAffineUV;
            // Where the object's origin is, for surface shaders.
            VARYING(4) flat out vec3 vObjectPosition;
            VARYING(5) out vec2 vLightmapUV;

            void main()
            {
                vec4 worldPosition = draw.model * vec4(aPosition, 1.0);
                vWorldPosition = worldPosition.xyz;

                // Normals turn with the object but stay square to its surface however it's stretched: by the cofactors of
                // its rotation and scale (the inverse transposed, but for size, which doesn't matter as it's normalised).
                mat3 m = mat3(draw.model);
                mat3 cofactors = mat3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1]));
                vNormal = cofactors * aNormal * sign(dot(m[0], cross(m[1], m[2])));

                vLightmapUV = aLightmapUV;
                vUV = aUV;
                vAffineUV = aUV;
                vObjectPosition = draw.model[3].xyz;
                vec4 position = frame.projection * frame.view * worldPosition;

                // Corners snapped to a coarse grid on the screen (Lighting.VertexSnapping), as the PlayStation's were to
                // whole pixels, so things wobble as they move. Not behind the camera, where it would fold them over.
                if (frame.retro.x > 0.0 && position.w > 0.0)
                {
                    float aspect = frame.projection[1][1] / frame.projection[0][0];
                    vec2 grid = vec2(frame.retro.x * aspect, frame.retro.x) * 0.5;
                    position.xy = round(position.xy / position.w * grid) / grid * position.w;
                }

                gl_Position = position;
            }
            """;

        // What the mesh shader hands on, and what the scene pass draws into.
        private const string SceneFragmentInputs = """
            VARYING(0) in vec3 vWorldPosition;
            VARYING(1) in vec3 vNormal;
            VARYING(2) in vec2 vUV;
            VARYING(3) noperspective in vec2 vAffineUV;
            VARYING(4) flat in vec3 vObjectPosition;
            VARYING(5) in vec2 vLightmapUV;
            // The draw's baked light, read straight from its constants (see DrawConstants).
            #define vBaked draw.lightmap
            #define vProbeRed draw.probe[0]
            #define vProbeGreen draw.probe[1]
            #define vProbeBlue draw.probe[2]

            // One colour of the probes' light for a surface facing n: its average, brighter towards where the light comes
            // from. Not straight average + dot(change, n), which goes below nothing away from a bright light, but a curve
            // with the same average and lean that never does (Hazel's, from Geomerics).
            float probeChannel(vec4 c, vec3 n)
            {
                float average = max(c.x, 0.0);
                vec3 lean = 0.5 * c.yzw;
                float leaning = length(lean);
                if (average <= 1e-6 || leaning <= 1e-6) return average;

                float ratio = min(leaning / average, 1.0);
                float q = 0.5 * (1.0 + dot(lean / leaning, n));
                float p = 1.0 + 2.0 * ratio;
                float a = (1.0 - ratio) / (1.0 + ratio);
                return average * (a + (1.0 - a) * (p + 1.0) * pow(max(q, 0.0), p));
            }

            // The light probes' light arriving from way n, for a surface facing it (see ProbeLight).
            vec3 probeLight(vec3 n)
            {
                return vec3(probeChannel(vProbeRed, n), probeChannel(vProbeGreen, n), probeChannel(vProbeBlue, n));
            }

            layout (location = 0) out vec4 FragColor;
            // The part of the colour that's soft light from all around, which ambient occlusion takes away from.
            layout (location = 1) out vec4 AmbientColor;
            // What glows, in linear light, for bloom: emission, so glowing things keep their colour on screen and still
            // light up the space round them. Nearer things cover glow behind them.
            layout (location = 2) out vec4 GlowColor;

            """;

        // ---- Surfaces ---------------------------------------------------------------------------------------------
        // How a surface looks at one point: from the material's settings, then changed by its surface shader if it has
        // one (see Shader), which defines void surface(inout Surface s) and can read the values below.
        private const string SurfaceCommon = """
            struct Surface
            {
                vec3 color;        // linear
                float alpha;       // with alphaClip: where it's below alphaClip, nothing's drawn (leaves, fences)
                vec3 normal;       // in the world, facing out of the surface
                float roughness;
                float metallic;
                float specular;
                vec3 emission;     // linear light it gives off; above 1 it glows
                float alphaClip;
            };

            vec2 uv;               // the mesh's texture coordinates, bent across the surface as much as Lighting.AffineTextures says
            vec3 worldPosition;
            vec3 worldNormal;      // the mesh's own, before any normal map
            vec3 viewDirection;    // from the surface towards the camera
            vec3 objectPosition;   // where the object's origin is
            vec2 screenPosition;   // 0 to 1 across the view, from its top-left corner
            float time;            // seconds since the game started

            // The material's own look, as the surface shader was given it.
            Surface materialSurface;

            // A texture's colour, made linear as the surface's colours are, in crisp texels if the scene asks.
            vec4 sampleColor(sampler2D image, vec2 at)
            {
                vec4 texel = crispTexture(image, at);
                return vec4(toLinear(texel.rgb), texel.a);
            }

            // A texture's values as they're stored, not made linear: masks, heights, noise.
            vec4 sampleData(sampler2D image, vec2 at) { return crispTexture(image, at); }

            // A normal map's bumps as a direction off the surface (z straight out), made as deep as strength says.
            vec3 sampleNormal(sampler2D image, vec2 at, float strength)
            {
                vec3 bent = texture(image, at).xyz * 2.0 - 1.0;
                bent.xy *= strength;
                return bent;
            }

            // A direction off the surface (as normal maps have them, z straight out) turned into the world. Which ways
            // across the surface its x and y go is worked out from how the position and texture coordinates `at` change
            // across the pixel, so meshes don't need tangents (Schüler's method).
            vec3 surfaceToWorld(vec3 bent, vec2 at)
            {
                vec3 N = worldNormal;
                vec3 dp1 = dFdx(worldPosition);
                vec3 dp2 = dFdy(worldPosition);
                vec2 duv1 = dFdx(at);
                vec2 duv2 = dFdy(at);
                vec3 dp2perp = cross(dp2, N);
                vec3 dp1perp = cross(N, dp1);
                vec3 T = dp2perp * duv1.x + dp1perp * duv2.x;
                vec3 B = dp2perp * duv1.y + dp1perp * duv2.y;
                float scale = inversesqrt(max(dot(T, T), dot(B, B)));
                if (!(scale < 1e18)) return N;

                return normalize(mat3(T * scale, B * scale, N) * bent);
            }

            vec3 surfaceToWorld(vec3 bent) { return surfaceToWorld(bent, uv); }

            void readSurfaceInputs()
            {
                uv = mix(vUV, vAffineUV, frame.retro.y);
                worldPosition = vWorldPosition;
                worldNormal = normalize(vNormal);
                viewDirection = normalize(frame.cameraPosition.xyz - vWorldPosition);
                objectPosition = vObjectPosition;
                vec4 clip = frame.projection * frame.view * vec4(vWorldPosition, 1.0);
                screenPosition = vec2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
                time = frame.cameraPosition.w;
            }

            // The look the material's own settings give.
            Surface standardSurface()
            {
                vec2 at = materialUV(vUV, vAffineUV);
                vec4 albedo = material.color;
                if (material.surface.w > 0.5)
                {
                    vec4 texel = materialTexel(at);
                    albedo *= vec4(toLinear(texel.rgb), texel.a);
                }

                Surface s;
                s.color = albedo.rgb;
                s.alpha = albedo.a;
                s.normal = worldNormal;
                if (material.normalParams.x > 0.5) s.normal = surfaceToWorld(sampleNormal(normalMap, at, material.normalParams.y), at);
                s.roughness = material.surface.x;
                s.metallic = material.surface.y;
                s.specular = material.surface.z;
                s.emission = material.emission.rgb;
                s.alphaClip = 0.0;
                return s;
            }

            """;

        // A surface shader's own settings: its values (in the material's block) and up to four textures.
        private const string SurfaceShaderHeader = """
            #define shaderValues material.values
            TEXTURE(1, 3) sampler2D shaderTexture0;
            TEXTURE(1, 4) sampler2D shaderTexture1;
            TEXTURE(1, 5) sampler2D shaderTexture2;
            TEXTURE(1, 6) sampler2D shaderTexture3;

            """;

        // The surface the pixel shows: the material's look, through its surface shader if it has one. Pixels it cuts
        // out aren't drawn.
        private const string ReadSurface = """
                readSurfaceInputs();
                Surface s = standardSurface();
            #ifdef CUSTOM_SURFACE
                materialSurface = s;
                surface(s);
                if (s.alpha < s.alphaClip) discard;
                s.normal = normalize(s.normal);
            #endif

            """;

        /// <summary>
        /// A scene fragment shader, lit or unlit: with a surface shader's code (see <see cref="Rendering.Shader.Body"/>)
        /// put in, numbered from its first line so the compiler's errors match it, or the standard one without.
        /// </summary>
        private static string SceneFragment(Rendering.Shader? surface, string shading, string main)
        {
            string custom = surface == null ? "" : SurfaceShaderHeader + "#line 1\n" + surface.Body + "\n";
            return (surface == null ? "" : "#define CUSTOM_SURFACE 1\n") + Frame + DrawConstants + Material + SceneFragmentInputs + SurfaceCommon + custom + shading + main;
        }

        public static readonly string LitFragment = LitFragmentWith(null);
        public static readonly string UnlitFragment = UnlitFragmentWith(null);

        /// <summary>The lit scene shader, with a material's surface shader in it (or the standard one, for null).</summary>
        public static string LitFragmentWith(Rendering.Shader? surface) => SceneFragment(surface, LitShading, "void main()\n{\n" + ReadSurface + LitMain);

        /// <summary>The unlit scene shader, with a material's surface shader in it (or the standard one, for null).</summary>
        public static string UnlitFragmentWith(Rendering.Shader? surface) => SceneFragment(surface, "", "void main()\n{\n" + ReadSurface + UnlitMain);

        private const string LitShading = """
            vec3 diffuseLight;
            vec3 specularLight;

            // ---- Shadows ----------------------------------------------------------------------------------------
            // A 5x5 tent filter from 9 lookups, each already blending 4 texels' tests in hardware (Castaño's), so
            // edges are soft and smooth without noise.
            float filteredShadow(vec2 shadowUV, float layer, float depth)
            {
                float texel = frame.shadow.x;
                vec2 position = shadowUV / texel;
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
                // Looked up from a little off the surface, so it doesn't shadow itself: by a texel or so of this cascade
                // (they get bigger further out), more where it turns away from the sun. Only the backs of things are in
                // the shadow map, so lit surfaces need little; any more, and where something sits on the ground, the
                // ground right beside it looks up from inside it, and is lit.
                float away = 1.0 - clamp(dot(N, L), 0.0, 1.0);
                float texel = frame.cascadeTexels[cascade];
                vec3 position = vWorldPosition + N * texel * (0.5 + 2.5 * away * away);

                vec4 lightSpace = frame.shadowMatrices[cascade] * vec4(position, 1.0);
                vec3 p = lightSpace.xyz / lightSpace.w;
                vec2 shadowUV = NDC_TO_UV(p.xy);
                if (any(lessThan(shadowUV, vec2(0.0))) || any(greaterThan(shadowUV, vec2(1.0))) || p.z >= 1.0) return 1.0;

                return filteredShadow(shadowUV, float(cascade), STORED_DEPTH(p.z) - 0.0002);
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

            """;

        private const string LitMain = """
                float roughness = clamp(s.roughness, 0.04, 1.0);
                float metallic = clamp(s.metallic, 0.0, 1.0);
                vec3 F0 = mix(vec3(0.08 * clamp(s.specular, 0.0, 1.0)), s.color, metallic);
                vec3 diffuseColor = s.color * (1.0 - metallic);

                vec3 N = s.normal;
                vec3 toCamera = frame.cameraPosition.xyz - vWorldPosition;
                float distance = length(toCamera);
                vec3 V = toCamera / max(distance, 1e-4);
                float NdotV = max(dot(N, V), 1e-4);

                diffuseLight = vec3(0.0);
                specularLight = vec3(0.0);

                int shadowLight = int(frame.shadow.y);
                for (int i = 0; i < frame.lightCounts.x; i++)
                {
                    // A baked one's light is in the lightmap and probes.
                    if (frame.directionalDirection[i].w > 0.5) continue;
                    vec3 L = normalize(-frame.directionalDirection[i].xyz);
                    vec3 color = frame.directionalColor[i].rgb;
                    if (i == shadowLight) color *= sunShadow(worldNormal, L);
                    addLight(N, V, L, color, F0, roughness);
                }

                ivec2 cluster = lightCluster(vWorldPosition);
                for (int k = 0; k < cluster.y; k++)
                {
                    int i = clusterLight(cluster.x + k);
                    if (frame.pointPositionRange[i].w < 0.0) continue;
                    vec3 L;
                    vec3 color = pointLight(i, vWorldPosition, L);
                    int shadow = lightShadow(i);
                    if (shadow >= 0 && color.r + color.g + color.b > 0.0) color *= localShadow(shadow, frame.pointPositionRange[i].xyz, vWorldPosition, worldNormal, true);
                    if (color.r + color.g + color.b > 0.0) addLight(N, V, L, color, F0, roughness);
                }

                // Light from all around, and the sky reflected: sharply in smooth surfaces, blurred to the average
                // light from that side in rough ones. Reflections that would point into the surface fade out.
                float ambientIntensity = frame.skyLight.w;
                vec3 R = reflect(-V, N);
                float blur = smoothstep(0.0, 0.7, roughness);
                vec3 around = ambientLight(N) * ambientIntensity;
                vec3 reflected = mix(skyRadiance(R), ambientLight(R), blur) * ambientIntensity;
                float ambientShare = 1.0;

                // Baked light, where there is some, in place of the sky's: from the lightmap or the probes round it, with
                // the sky's shadows, the bounce and the baked lights in it. The sky's reflection is kept out of where
                // its light didn't reach (indoors, under things), and rough surfaces reflect the baked light instead.
                if (vBaked.x > 0.5 || vBaked.y > 0.5)
                {
                    vec3 bakedAround = vBaked.x > 0.5 ? bakedLightAt(vLightmapUV) : probeLight(N);
                    vec3 bakedReflected = vBaked.x > 0.5 ? bakedAround : probeLight(R);
                    float open = clamp(dot(bakedReflected, vec3(0.3, 0.6, 0.1)) / max(dot(ambientLight(R) * ambientIntensity, vec3(0.3, 0.6, 0.1)), 1e-4), 0.0, 1.0);
                    around = bakedAround;
                    reflected = mix(skyRadiance(R) * ambientIntensity * open, bakedReflected, blur);
                    // The lightmap's corners are already darkened, so ambient occlusion only adds a little to them.
                    if (vBaked.x > 0.5) ambientShare = 0.5;
                }

                reflected *= clamp(1.0 + dot(R, worldNormal), 0.0, 1.0);
                vec2 brdf = environmentBRDF(roughness, NdotV);
                vec3 ambient = diffuseColor * around + reflected * (F0 * brdf.x + brdf.y);

                vec3 emission = max(s.emission, vec3(0.0));
                vec3 color = diffuseColor * diffuseLight + specularLight + ambient + emission;

                // Fades into the sky with distance. Done last, so far-off things match the sky behind them. Then the
                // volumetric fog in front of it, if there is any: what it hides, and the light it adds.
                float fog = fogAmount(distance);
                vec4 haze = fogAtPoint(vWorldPosition);
                FragColor = vec4(mix(color, fogColor(-V), fog) * haze.a + haze.rgb, 1.0);
                AmbientColor = vec4(ambient * (ambientShare * (1.0 - fog) * haze.a), 1.0);
                GlowColor = vec4(emission * (GLOW_STRENGTH * (1.0 - fog) * haze.a), 1.0);
            }
            """;

        private const string UnlitMain = """
                vec3 emission = max(s.emission, vec3(0.0));
                vec3 color = s.color + emission;

                vec3 toCamera = frame.cameraPosition.xyz - vWorldPosition;
                float distance = length(toCamera);
                float fog = fogAmount(distance);
                vec4 haze = fogAtPoint(vWorldPosition);
                FragColor = vec4(mix(color, fogColor(-toCamera / max(distance, 1e-4)), fog) * haze.a + haze.rgb, 1.0);
                AmbientColor = vec4(0.0, 0.0, 0.0, 1.0);
                GlowColor = vec4(emission * (GLOW_STRENGTH * (1.0 - fog) * haze.a), 1.0);
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

                // Seen through all of the volumetric fog.
                vec4 haze = fogBetween(NDC_TO_UV(vScreen), 1e9);
                FragColor = vec4(color * haze.a + haze.rgb, 1.0);
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
            layout (location = 5) in float aLook;

            VARYING(0) out vec2 vCorner;
            VARYING(1) out vec4 vColor;
            VARYING(2) out float vGlow;
            VARYING(3) out float vAdditive;
            VARYING(4) out vec3 vToCamera;
            VARYING(5) out float vLook;

            void main()
            {
                vCorner = aCorner;
                vColor = aColor;
                vGlow = aGlow;
                vAdditive = aAdditive;
                vToCamera = frame.cameraPosition.xyz - aPosition;
                vLook = aLook;
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
            layout (location = 5) out float vLook;

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
                vLook = 0.0;

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

        // A soft round dot (or another shape: see ParticleLook), blended with premultiplied alpha: an alpha of 0 adds
        // light, anything else covers what's behind. Glowing ones are brighter than white, so they bloom.
        public const string ParticleFragment = Frame + """
            VARYING(0) in vec2 vCorner;
            VARYING(1) in vec4 vColor;
            VARYING(2) in float vGlow;
            VARYING(3) in float vAdditive;
            VARYING(4) in vec3 vToCamera;
            VARYING(5) in float vLook;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 AmbientColor;
            // What glows, in linear light, for bloom: emission, so glowing things keep their colour on screen and still
            // light up the space round them. Nearer things cover glow behind them.
            layout (location = 2) out vec4 GlowColor;

            void main()
            {
                // The shape drawn (see ParticleLook): a soft dot, a disc, a square, a ring, or a spark with four rays.
                float look = floor(vLook + 0.5);
                vec2 c = vCorner;
                float r2 = dot(c, c);
                float shape;
                if (look < 0.5)
                {
                    if (r2 >= 1.0) discard;
                    shape = (1.0 - r2) * (1.0 - r2);
                }
                else if (look < 1.5) shape = clamp((1.0 - sqrt(r2)) * 12.0, 0.0, 1.0);
                else if (look < 2.5) shape = clamp((1.0 - max(abs(c.x), abs(c.y))) * 12.0, 0.0, 1.0);
                else if (look < 3.5) shape = clamp(1.0 - abs(sqrt(r2) - 0.75) * 6.0, 0.0, 1.0);
                else
                {
                    float rays = max(1.0 - abs(c.x) * 8.0, 0.0) * (1.0 - abs(c.y)) + max(1.0 - abs(c.y) * 8.0, 0.0) * (1.0 - abs(c.x));
                    shape = clamp(pow(max(1.0 - sqrt(r2), 0.0), 3.0) + rays, 0.0, 1.0);
                }

                if (shape <= 0.002) discard;
                float alpha = vColor.a * shape;
                float covers = alpha * (1.0 - vAdditive);

                // In the distance, dust fades into the fog and glowing things fade out.
                float distance = length(vToCamera);
                float fog = fogAmount(distance);
                vec3 linearColor = toLinear(vColor.rgb);
                vec3 color = mix(linearColor, fogColor(-vToCamera / max(distance, 1e-4)), fog * (1.0 - vAdditive)) * mix(1.0, 1.0 - fog, vAdditive);

                // Volumetric fog in front: it hides some of the particle, and adds its own light where the particle
                // covers what's behind (which already has the fog in front of it).
                vec4 haze = fogAtPoint(frame.cameraPosition.xyz - vToCamera);
                FragColor = vec4(color * alpha * haze.a + haze.rgb * covers, covers);
                AmbientColor = vec4(0.0, 0.0, 0.0, covers);
                GlowColor = vec4(linearColor * (vGlow * alpha * (1.0 - fog) * haze.a), covers);
            }
            """;

        // ---- Sprites and world text ---------------------------------------------------------------------------------
        // Flat pieces of picture in the world (see WorldQuad), built on the CPU. Colours are straight sRGB; textures are
        // straight alpha, as image files are, unless the corner says they're premultiplied, as the font's letters are.
        public const string SpriteVertex = Frame + """
            layout (location = 0) in vec3 aPosition;
            layout (location = 1) in vec2 aUV;
            layout (location = 2) in vec4 aColor;
            layout (location = 3) in float aPremultiplied;

            VARYING(0) out vec2 vUV;
            VARYING(1) out vec4 vColor;
            VARYING(2) out float vPremultiplied;
            VARYING(3) out vec3 vToCamera;

            void main()
            {
                vUV = aUV;
                vColor = aColor;
                vPremultiplied = aPremultiplied;
                vToCamera = frame.cameraPosition.xyz - aPosition;
                gl_Position = frame.projection * frame.view * vec4(aPosition, 1.0);
            }
            """;

        // Blended with premultiplied alpha over the scene, and into its ambient and glow images, so a sprite covers the
        // light and glow behind it as much as it covers its colour.
        public const string SpriteFragment = Frame + """
            TEXTURE(1, 0) sampler2D spriteTexture;

            VARYING(0) in vec2 vUV;
            VARYING(1) in vec4 vColor;
            VARYING(2) in float vPremultiplied;
            VARYING(3) in vec3 vToCamera;

            layout (location = 0) out vec4 FragColor;
            layout (location = 1) out vec4 AmbientColor;
            layout (location = 2) out vec4 GlowColor;

            void main()
            {
                vec4 texel = texture(spriteTexture, vUV);
                if (vPremultiplied > 0.5 && texel.a > 0.0) texel.rgb /= texel.a;

                float alpha = texel.a * vColor.a;
                if (alpha <= 0.004) discard;

                vec3 color = toLinear(texel.rgb * vColor.rgb);
                float distance = length(vToCamera);
                float fog = fogAmount(distance);
                color = mix(color, fogColor(-vToCamera / max(distance, 1e-4)), fog);

                vec4 haze = fogAtPoint(frame.cameraPosition.xyz - vToCamera);
                FragColor = vec4((color * haze.a + haze.rgb) * alpha, alpha);
                AmbientColor = vec4(0.0, 0.0, 0.0, alpha);
                GlowColor = vec4(0.0, 0.0, 0.0, alpha);
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
                // On the ground, or (2D) standing in the X-Y plane behind the game. Only as big as it can be seen (see the
                // fade below): smaller triangles keep their depth exact.
                vec3 eye = frame.cameraPosition.xyz;
                if (frame.shadow.w > 0.5)
                {
                    vec2 corner = CORNERS[VERTEX_ID] * 2000.0;
                    vWorld = vec3(eye.x + corner.x, eye.y + corner.y, 0.0);
                }
                else
                {
                    vec2 corner = CORNERS[VERTEX_ID] * min(2000.0, 160.0 + abs(eye.y) * 14.0);
                    vWorld = vec3(eye.x + corner.x, 0.0, eye.z + corner.y);
                }

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
                bool flat2D = frame.shadow.w > 0.5;
                vec2 position = flat2D ? vWorld.xy : vWorld.xz;
                vec2 metresPerPixel = fwidth(position);
                float crowding = max(metresPerPixel.x, metresPerPixel.y);

                float minor = lines(position, 1.0) * (1.0 - smoothstep(0.04, 0.2, crowding));
                float major = lines(position, 10.0) * (1.0 - smoothstep(0.4, 2.0, crowding));
                float alpha = max(minor * 0.22, major * 0.42);

                // The axes, a little bolder: X red, and Z blue on the ground or Y green in 2D.
                float xAxis = 1.0 - min(abs(position.y) / max(metresPerPixel.y * 1.5, 1e-5), 1.0);
                float otherAxis = 1.0 - min(abs(position.x) / max(metresPerPixel.x * 1.5, 1e-5), 1.0);
                vec3 color = vec3(0.8);
                color = mix(color, vec3(0.85, 0.16, 0.12), xAxis);
                color = mix(color, flat2D ? vec3(0.3, 0.75, 0.2) : vec3(0.14, 0.32, 0.95), otherAxis);
                alpha = max(alpha, max(xAxis, otherAxis) * 0.75);

                // On the ground, it fades out with distance, further when the camera is higher up. In 2D it's seen flat on.
                if (!flat2D)
                {
                    float height = abs(frame.cameraPosition.y);
                    float distance = length(frame.cameraPosition.xyz - vWorld);
                    alpha *= 1.0 - smoothstep(30.0 + height * 4.0, 120.0 + height * 12.0, distance);
                }

                FragColor = vec4(color * alpha, alpha);
                AmbientColor = vec4(0.0);
                GlowColor = vec4(0.0);

                // Its depth worked out exactly here. On the ground, a little nearer than it is (a fifth of a percent of the
                // way to the camera), so it shows on a floor at the same height instead of flickering against it; in 2D a
                // little further, so it stays behind what's in the plane.
                vec4 clip = frame.projection * frame.view * vec4(mix(vWorld, frame.cameraPosition.xyz, flat2D ? -0.0005 : 0.002), 1.0);
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
                float orthographic;    // 1 for an orthographic camera, whose depth is linear
                float pad1;
            #ifdef VERTEX_VULKAN
            } post;
            #else
            };
            uniform PostBlock post;
            #endif

            VARYING(0) in vec2 vUV;
            layout (location = 0) out vec4 FragColor;

            // How far in front of the camera the scene is here. With perspective, depth d is (M33 z + M43) / -z, so
            // z = -M43 / (d + M33); orthographic, d is M33 z + M43, so z = (d - M43) / M33.
            float viewDepth(vec2 uv)
            {
                float d = NDC_DEPTH(textureLod(depthImage, uv, 0.0).r);
                if (post.orthographic > 0.5) return (post.projection.w - d) / post.projection.z;
                return post.projection.w / (d + post.projection.z);
            }

            vec3 viewPosition(vec2 uv)
            {
                float z = viewDepth(uv);
                vec2 ndc = UV_TO_NDC(uv);
                if (post.orthographic > 0.5) return vec3(ndc.x / post.projection.x, ndc.y / post.projection.y, -z);
                return vec3(ndc.x * z / post.projection.x, ndc.y * z / post.projection.y, -z);
            }

            // Where a point in front of the camera (view space) is on the screen, 0 to 1 across it.
            vec2 viewToUV(vec3 p)
            {
                vec2 ndc = vec2(p.x * post.projection.x, p.y * post.projection.y);
                return NDC_TO_UV(post.orthographic > 0.5 ? ndc : ndc / -p.z);
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

                    vec2 sampleUV = viewToUV(s);
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

        // ---- Post effects written for a game (see Shader and PostEffect) --------------------------------------------

        // What a game's post effect can use. Its uv counts from the top-left of the picture in both APIs (OpenGL's images
        // start at the bottom), and picture() and distanceAt() take the same. Its settings come in shaderValues and its
        // textures in shaderTexture0 to 3, which each setting's name stands for (see Shader.SetCode).
        public const string EffectHeader = Post + """
            #ifdef VERTEX_VULKAN
            layout (set = 1, binding = 0, std140) uniform EffectData { vec4 shaderValues[16]; };
            #else
            uniform vec4 shaderValues[16];
            #endif
            TEXTURE(1, 1) sampler2D shaderTexture0;
            TEXTURE(1, 2) sampler2D shaderTexture1;
            TEXTURE(1, 3) sampler2D shaderTexture2;
            TEXTURE(1, 4) sampler2D shaderTexture3;

            vec2 screenSize;   // in pixels
            float time;        // seconds since the game started

            vec2 effectToImage(vec2 uv)
            {
            #ifdef VERTEX_VULKAN
                return uv;
            #else
                return vec2(uv.x, 1.0 - uv.y);
            #endif
            }

            // The picture so far, in screen colours (0 to 1).
            vec4 picture(vec2 uv) { return textureLod(source, effectToImage(uv), 0.0); }

            // How far from the camera what's drawn at uv is, along its view; very far for the sky.
            float distanceAt(vec2 uv) { return viewDepth(effectToImage(uv)); }

            """;

        public const string EffectMain = """

            void main()
            {
                screenSize = 1.0 / post.texelSize;
                time = post.time;
                FragColor = vec4(clamp(effect(effectToImage(vUV)).rgb, 0.0, 1.0), 1.0);
            }
            """;

        /// <summary>A game's post effect, built into a whole fragment shader: <see cref="EffectHeader"/>, its code, then a main that calls it.</summary>
        public static string EffectFragment(Shader shader) => EffectHeader + "#line 1\n" + shader.Body + EffectMain;

        /// <summary>
        /// An OpenGL driver's compile errors with the places in them named by <paramref name="file"/> and line, as Vulkan's
        /// compiler names them: drivers write "0:4:" (AMD, Intel, Mesa) or "0(4) :" (NVIDIA).
        /// </summary>
        public static string WithFileName(string errors, string file) =>
            System.Text.RegularExpressions.Regex.Replace(errors, @"\b0(?::(\d+):|\((\d+)\)\s*:)", match => $"{file}:{match.Groups[1].Value}{match.Groups[2].Value}:");
    }
}
