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
            uniform vec3 uAmbientLight;
            uniform int uDirectionalLightCount;
            uniform DirectionalLight uDirectionalLights[MAX_DIR_LIGHTS];
            uniform int uPointLightCount;
            uniform PointLight uPointLights[MAX_POINT_LIGHTS];

            out vec4 FragColor;

            vec3 diffuseLight;
            vec3 specularLight;

            void addLight(vec3 N, vec3 V, vec3 L, vec3 color)
            {
                float NdotL = max(dot(N, L), 0.0);
                if (NdotL <= 0.0) return;

                vec3 H = normalize(L + V);
                diffuseLight += color * NdotL;
                specularLight += color * pow(max(dot(N, H), 0.0), uShininess) * uSpecular;
            }

            void main()
            {
                vec4 albedo = uColor;
                if (uHasTexture) albedo *= texture(uTexture, vUV);

                vec3 N = normalize(vNormal);
                vec3 V = normalize(uCameraPosition - vWorldPosition);

                diffuseLight = uAmbientLight;
                specularLight = vec3(0.0);

                for (int i = 0; i < uDirectionalLightCount; i++)
                {
                    addLight(N, V, normalize(-uDirectionalLights[i].direction), uDirectionalLights[i].color);
                }

                for (int i = 0; i < uPointLightCount; i++)
                {
                    vec3 toLight = uPointLights[i].position - vWorldPosition;
                    float distance = length(toLight);
                    float falloff = clamp(1.0 - distance / uPointLights[i].range, 0.0, 1.0);
                    addLight(N, V, toLight / distance, uPointLights[i].color * falloff * falloff);
                }

                FragColor = vec4(albedo.rgb * diffuseLight + specularLight, albedo.a);
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
    }
}
