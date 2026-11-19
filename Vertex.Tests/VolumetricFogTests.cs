using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Shaderc;

using Vertex.Rendering;
using Vertex.Rendering.Shaders;
using Vertex.Rendering.Vulkan;
using Vertex.Serialization;

namespace Vertex.Tests
{
    /// <summary>Spot lights, the many lights a scene can have, volumetric fog and fog volumes.</summary>
    public class VolumetricFogTests : EngineTest
    {
        private readonly RendererTests.RecordingBackend _backend = new();
        private readonly Renderer _renderer;
        private readonly Camera _camera;

        public VolumetricFogTests()
        {
            _renderer = new Renderer(_backend);
            _renderer.Resize(1280, 720);

            // Looking along -Z from just above the ground.
            var camera = new GameObject("Camera");
            camera.Transform.Position = new Vector3(0f, 2f, 0f);
            _camera = camera.AddComponent<Camera>();
        }

        private void Render()
        {
            Scene.Active.Update();
            _renderer.Render(Scene.Active);
        }

        private static Light AddLight(LightType type, Vector3 position, float range = 10f)
        {
            var gameObject = new GameObject("Light");
            gameObject.Transform.Position = position;
            Light light = gameObject.AddComponent<Light>();
            light.Type = type;
            light.Range = range;
            return light;
        }

        [Fact]
        public void Every_shared_shader_compiles_for_Vulkan()
        {
            // OpenGL's are the same code with a different header; Vulkan's compiler is the stricter of the two.
            using var compiler = new VkShaderCompiler(null!);
            (string Name, ShaderKind Kind, string Source)[] shaders =
            [
                ("mesh.vert", ShaderKind.VertexShader, ShaderLibrary.MeshVertex),
                ("lit.frag", ShaderKind.FragmentShader, ShaderLibrary.LitFragment),
                ("unlit.frag", ShaderKind.FragmentShader, ShaderLibrary.UnlitFragment),
                ("shadow.vert", ShaderKind.VertexShader, ShaderLibrary.ShadowVertex),
                ("sky.vert", ShaderKind.VertexShader, ShaderLibrary.SkyVertex),
                ("sky.frag", ShaderKind.FragmentShader, ShaderLibrary.SkyFragment),
                ("grid.vert", ShaderKind.VertexShader, ShaderLibrary.GridVertex),
                ("grid.frag", ShaderKind.FragmentShader, ShaderLibrary.GridFragment),
                ("particle.vert", ShaderKind.VertexShader, ShaderLibrary.ParticleVertex),
                ("gpuparticle.vert", ShaderKind.VertexShader, ShaderLibrary.GpuParticleVertex),
                ("particle.frag", ShaderKind.FragmentShader, ShaderLibrary.ParticleFragment),
                ("sprite.vert", ShaderKind.VertexShader, ShaderLibrary.SpriteVertex),
                ("sprite.frag", ShaderKind.FragmentShader, ShaderLibrary.SpriteFragment),
                ("fullscreen.vert", ShaderKind.VertexShader, ShaderLibrary.FullscreenVertex),
                ("foglight.frag", ShaderKind.FragmentShader, ShaderLibrary.FogLightFragment),
                ("fogtotal.frag", ShaderKind.FragmentShader, ShaderLibrary.FogTotalFragment),
                ("bloomdown.frag", ShaderKind.FragmentShader, ShaderLibrary.BloomDownsampleFragment),
                ("bloomup.frag", ShaderKind.FragmentShader, ShaderLibrary.BloomUpsampleFragment),
                ("occlusion.frag", ShaderKind.FragmentShader, ShaderLibrary.OcclusionFragment),
                ("occlusionblur.frag", ShaderKind.FragmentShader, ShaderLibrary.OcclusionBlurFragment),
                ("composite.frag", ShaderKind.FragmentShader, ShaderLibrary.CompositeFragment),
                ("antialias.frag", ShaderKind.FragmentShader, ShaderLibrary.AntiAliasFragment)
            ];

            foreach ((string name, ShaderKind kind, string source) in shaders)
            {
                byte[] spirv = compiler.CompileNow(name, kind, ShaderLibrary.Build(ShaderTarget.Vulkan, source));
                Assert.NotEmpty(spirv);
            }
        }

        [Fact]
        public void The_frame_data_is_laid_out_in_whole_four_float_steps()
        {
            // std140 puts every vec4 and mat4 on a 16-byte boundary; the C# side has to match it exactly.
            Assert.Equal(0, Unsafe.SizeOf<FrameUniforms>() % 16);
            int lights = ShaderLibrary.MaxPointLights * 16;
            int offsetOfSpot = (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.PointSpot));
            int offsetOfColor = (int)Marshal.OffsetOf<FrameUniforms>(nameof(FrameUniforms.PointColor));
            Assert.Equal(lights, offsetOfSpot - offsetOfColor);
        }

        [Fact]
        public void A_spot_light_shines_in_a_cone_along_its_forward_direction()
        {
            Light spot = AddLight(LightType.Spot, new Vector3(0f, 5f, -10f));
            spot.SpotAngle = 60f;
            spot.SpotSoftness = 0.5f;
            spot.Transform.EulerAngles = new Vector3(-90f, 0f, 0f);

            Render();

            PointLightData data = Assert.Single(_backend.LastPointLights);
            Assert.True(data.IsSpot);
            Assert.Equal(-1f, data.Direction.Y, 3);
            Assert.Equal(MathF.Cos(30f * MathF.PI / 180f), data.SpotOuter, 4);
            Assert.Equal(MathF.Cos(15f * MathF.PI / 180f), data.SpotInner, 4);

            // Packed for the shader: straight down the middle is full, at the edge nothing.
            Vector4 cone = _backend.LastUniforms.PointSpot[0];
            float Cone(Vector3 towards) => Math.Clamp(Vector3.Dot(towards, new Vector3(cone.X, cone.Y, cone.Z)) + cone.W, 0f, 1f);
            Assert.Equal(1f, Cone(-Vector3.UnitY), 3);
            Assert.Equal(0f, Cone(Vector3.Normalize(new Vector3(MathF.Sin(0.6f), -MathF.Cos(0.6f), 0f))), 3);
        }

        [Fact]
        public void A_point_light_shines_every_way()
        {
            AddLight(LightType.Point, new Vector3(0f, 2f, -10f));

            Render();

            Assert.False(Assert.Single(_backend.LastPointLights).IsSpot);
            Assert.Equal(new Vector4(0f, 0f, 0f, 1f), _backend.LastUniforms.PointSpot[0]);
        }

        [Fact]
        public void Lights_that_cant_reach_into_view_are_left_out()
        {
            AddLight(LightType.Point, new Vector3(0f, 2f, -10f));
            AddLight(LightType.Point, new Vector3(0f, 2f, 40f));

            // Behind the camera, but near enough to light what's in front of it.
            AddLight(LightType.Point, new Vector3(0f, 2f, 5f), range: 12f);

            Render();

            Assert.Equal(2, _backend.LastPointLights.Count);
            Assert.DoesNotContain(_backend.LastPointLights, light => light.Position.Z > 30f);
        }

        [Fact]
        public void With_more_lights_than_the_shaders_take_the_nearest_are_kept()
        {
            for (int i = 0; i < ShaderLibrary.MaxPointLights + 20; i++) AddLight(LightType.Point, new Vector3(0f, 2f, -5f - i * 3f), range: 2f);

            Render();

            Assert.Equal(ShaderLibrary.MaxPointLights, _backend.LastPointLights.Count);
            float furthest = _backend.LastPointLights.Max(light => -light.Position.Z);
            Assert.True(furthest <= 5f + (ShaderLibrary.MaxPointLights - 1) * 3f + 0.01f);
        }

        [Fact]
        public void Volumetric_fog_is_off_until_turned_on()
        {
            Render();
            Assert.Equal(0f, _backend.LastUniforms.FogReach.Z);

            Scene.Active.Lighting.VolumetricFog = true;
            Scene.Active.Lighting.VolumetricDensity = 0.05f;
            Scene.Active.Lighting.VolumetricDistance = 60f;
            Render();

            FrameUniforms uniforms = _backend.LastUniforms;
            Assert.Equal(1f, uniforms.FogReach.Z);
            Assert.Equal(60f, uniforms.FogReach.X);
            Assert.Equal(0.05f, uniforms.FogShape.X);
        }

        [Fact]
        public void An_orthographic_camera_has_no_volumetric_fog()
        {
            Scene.Active.Lighting.VolumetricFog = true;
            _camera.Projection = CameraProjection.Orthographic;

            Render();

            Assert.Equal(0f, _backend.LastUniforms.FogReach.Z);
        }

        [Fact]
        public void How_brightly_a_light_shows_in_the_fog_goes_to_the_shaders()
        {
            Light light = AddLight(LightType.Point, new Vector3(0f, 2f, -10f));
            light.FogStrength = 0.25f;

            Render();

            Assert.Equal(0.25f, _backend.LastUniforms.PointColor[0].W);
        }

        [Fact]
        public void Fog_volumes_in_reach_go_to_the_shaders_nearest_first()
        {
            Scene.Active.Lighting.VolumetricFog = true;
            for (int i = 0; i < ShaderLibrary.MaxFogVolumes + 3; i++)
            {
                var box = new GameObject("Mist");
                box.Transform.Position = new Vector3(0f, 1f, -4f - i * 6f);
                box.Transform.Scale = new Vector3(4f, 2f, 4f);
                box.AddComponent<FogVolume>().Density = 0.2f;
            }

            Render();

            Assert.Equal(ShaderLibrary.MaxFogVolumes, _backend.LastFogVolumes.Count);
            Assert.Equal(ShaderLibrary.MaxFogVolumes, _backend.LastUniforms.FogVolumeCount);

            // Each is known by where its middle is: into its box, that's the box's middle.
            bool HasBoxAt(float z) => _backend.LastFogVolumes.Any(volume => Vector3.Transform(new Vector3(0f, 1f, z), volume.WorldToBox).Length() < 1e-3f);
            Assert.True(HasBoxAt(-4f));
            Assert.False(HasBoxAt(-4f - (ShaderLibrary.MaxFogVolumes + 2) * 6f));
        }

        [Fact]
        public void Fog_volumes_are_left_out_without_volumetric_fog()
        {
            var box = new GameObject("Mist");
            box.Transform.Position = new Vector3(0f, 1f, -6f);
            box.AddComponent<FogVolume>();

            Render();

            Assert.Empty(_backend.LastFogVolumes);
        }

        [Fact]
        public void Each_frame_carries_on_from_the_last_unless_the_images_are_new()
        {
            var fog = new VolumetricFog();
            var frame = new RenderFrame { ViewMatrix = Matrix4x4.CreateTranslation(1f, 0f, 0f), ProjectionMatrix = Matrix4x4.Identity };
            var uniforms = new FrameUniforms();

            fog.Prepare(ref uniforms, frame, 160, 90, fresh: true);
            Assert.Equal(0f, uniforms.FogReach.W);
            Assert.Equal(new Vector4(160f, 90f, ShaderLibrary.FogSlices, 0f), uniforms.FogGrid);
            int first = fog.Current;

            Matrix4x4 before = frame.ViewMatrix * frame.ProjectionMatrix;
            frame.ViewMatrix = Matrix4x4.CreateTranslation(2f, 0f, 0f);
            fog.Prepare(ref uniforms, frame, 160, 90, fresh: false);
            Assert.Equal(VolumetricFog.Keep, uniforms.FogReach.W);
            Assert.Equal(before, uniforms.PreviousViewProjection);
            Assert.NotEqual(first, fog.Current);

            fog.Forget();
            fog.Prepare(ref uniforms, frame, 160, 90, fresh: false);
            Assert.Equal(0f, uniforms.FogReach.W);
        }

        [Fact]
        public void The_fog_grid_is_a_cell_for_every_few_pixels()
        {
            Assert.Equal((160, 90), VolumetricFog.GridSize(1280, 720));
            (int width, int height) = VolumetricFog.GridSize(3840, 2160);
            Assert.True(height <= 136 && width <= 241);
        }

        [Fact]
        public void Volumetric_fog_settings_and_lights_are_saved_with_the_scene()
        {
            Lighting lighting = Scene.Active.Lighting;
            lighting.VolumetricFog = true;
            lighting.VolumetricDensity = 0.07f;
            lighting.VolumetricWind = new Vector3(1f, 0f, 2f);
            Light spot = AddLight(LightType.Spot, Vector3.Zero);
            spot.SpotAngle = 33f;
            spot.FogStrength = 2f;
            var box = new GameObject("Mist");
            box.AddComponent<FogVolume>().Density = -0.02f;

            string json = SceneFile.Save(Scene.Active);
            var loaded = new Scene();
            SceneFile.LoadJson(json, loaded);
            loaded.ApplyChanges();

            Assert.True(loaded.Lighting.VolumetricFog);
            Assert.Equal(0.07f, loaded.Lighting.VolumetricDensity);
            Assert.Equal(new Vector3(1f, 0f, 2f), loaded.Lighting.VolumetricWind);
            Light light = loaded.FindComponents<Light>().Single(l => l.Type == LightType.Spot);
            Assert.Equal(33f, light.SpotAngle);
            Assert.Equal(2f, light.FogStrength);
            Assert.Equal(-0.02f, loaded.FindComponents<FogVolume>().Single().Density);
        }
    }
}
