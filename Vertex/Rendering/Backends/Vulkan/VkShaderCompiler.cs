using System.Security.Cryptography;
using System.Text;

using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// Turns the backend's GLSL into SPIR-V, the form Vulkan takes shaders in, with shaderc. Compiling takes a moment,
    /// so the results are kept in a cache folder by the hash of their source: only shaders that changed are compiled again.
    /// </summary>
    internal sealed unsafe class VkShaderCompiler : IDisposable
    {
        private static readonly string _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vertex", "ShaderCache");

        private readonly VkContext _context;
        private Shaderc? _shaderc;
        private Compiler* _compiler;
        private CompileOptions* _options;

        public VkShaderCompiler(VkContext context)
        {
            _context = context;
        }

        /// <summary>A shader module from GLSL <paramref name="source"/>. <paramref name="name"/> is only for error messages.</summary>
        public ShaderModule Create(string name, ShaderKind kind, string source)
        {
            byte[] spirv = Compile(name, kind, source);

            fixed (byte* code = spirv)
            {
                ShaderModuleCreateInfo info = new()
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code
                };
                VkContext.Check(_context.Vk.CreateShaderModule(_context.Device, &info, null, out ShaderModule module), $"create shader {name}");
                return module;
            }
        }

        private byte[] Compile(string name, ShaderKind kind, string source)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{source}")))[..32];
            string cachePath = Path.Combine(_cacheFolder, hash + ".spv");

            try
            {
                if (File.Exists(cachePath)) return File.ReadAllBytes(cachePath);
            }
            catch (IOException)
            {
                // Can't read the cache: compile it instead.
            }

            byte[] spirv = CompileNow(name, kind, source);

            try
            {
                Directory.CreateDirectory(_cacheFolder);
                File.WriteAllBytes(cachePath, spirv);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Only slower next time.
            }

            return spirv;
        }

        private byte[] CompileNow(string name, ShaderKind kind, string source)
        {
            if (_shaderc == null)
            {
                _shaderc = Shaderc.GetApi();
                _compiler = _shaderc.CompilerInitialize();
                _options = _shaderc.CompileOptionsInitialize();
                _shaderc.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
                _shaderc.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Performance);
            }

            byte[] text = Encoding.UTF8.GetBytes(source);
            fixed (byte* sourcePointer = text)
            {
                CompilationResult* result = _shaderc.CompileIntoSpv(_compiler, sourcePointer, (nuint)text.Length, kind, name, "main", _options);
                try
                {
                    if (_shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                        throw new InvalidOperationException($"Shader {name} didn't compile:\n{_shaderc.ResultGetErrorMessageS(result)}");

                    var spirv = new byte[(int)_shaderc.ResultGetLength(result)];
                    new ReadOnlySpan<byte>(_shaderc.ResultGetBytes(result), spirv.Length).CopyTo(spirv);
                    return spirv;
                }
                finally
                {
                    _shaderc.ResultRelease(result);
                }
            }
        }

        public void Dispose()
        {
            if (_shaderc == null) return;

            _shaderc.CompileOptionsRelease(_options);
            _shaderc.CompilerRelease(_compiler);
            _shaderc.Dispose();
            _shaderc = null;
        }
    }
}
