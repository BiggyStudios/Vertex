using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

using System;

namespace Vertex.Engine.Rendering
{
    public class Shader
    {
        public readonly int Handle;

        private readonly Dictionary<string, int> _uniformLocation;

        public Shader(string vertPath, string fragPath)
        {
            //Get the vertex shader data
            var shaderSource = File.ReadAllText(vertPath);
            //Create a vertex shader
            var vertexShader = GL.CreateShader(ShaderType.VertexShader);

            //Bind the vertex shader to the shadersource
            GL.ShaderSource(vertexShader, shaderSource);
            //Compile the shader the fun stuff :D
            CompileShader(vertexShader);
            
            //Now get the fragment shader
            shaderSource = File.ReadAllText(fragPath);
            //Create the fragment shader
            var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);

            //Bind the fragment shader as before with the vertex shader
            GL.ShaderSource(fragmentShader, shaderSource);
            //And compile the thing
            CompileShader(fragmentShader);

            //This merges the 2 shaders into a shader program
            Handle = GL.CreateProgram();

            //Attach the shaders to the shader program
            GL.AttachShader(Handle, vertexShader);
            GL.AttachShader(Handle, fragmentShader);

            //Link the shaders together
            LinkProgram(Handle);

            //Once the shader program is linked it no longer needs the shaders attached to it as the compiled code is copied into the shader program
            //Detach and delete the
            GL.DetachShader(Handle, vertexShader);
            GL.DetachShader(Handle, fragmentShader);
            GL.DeleteShader(fragmentShader);
            GL.DeleteShader(vertexShader);

            //Get the active number of uniforms in the shader
            GL.GetProgram(Handle, GetProgramParameterName.ActiveUniforms, out var numberOfUniforms);

            //Allocate dictionary to hold locations
            _uniformLocation = new Dictionary<string, int>();

            //Loop over all the uniforms
            for (var i = 0; i < numberOfUniforms; i++)
            {   
                //Get the name of this uniform
                var key = GL.GetActiveUniform(Handle, i, out _, out _);
                //Get the location
                var location = GL.GetUniformLocation(Handle, key);

                //And add it to the dictionary
                _uniformLocation.Add(key, location);
            }
        }

        private static void CompileShader(int shader)
        {
            //Try to compile the shader
            GL.CompileShader(shader);

            //Check for compilation errors
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var code);
            if (code != (int)All.True)
            {
                //Get info about the error
                var infoLog = GL.GetShaderInfoLog(shader);
                throw new Exception($"Error occured whilst compiling Shader({shader}).\n\n{infoLog}");
            }
        }

        private static void LinkProgram(int program)
        {
            //Link the program
            GL.LinkProgram(program);

            //Check for linking errors
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var code);
            if (code != (int)All.True)
            {
                throw new Exception($"Error occurred whilst linking program{program}");
            }
        }

        //A wrapper function that enables the shader program
        public void Use()
        {
            GL.UseProgram(Handle);
        }

        public int GetAttribLocation(string attribName)
        {
            return GL.GetAttribLocation(Handle, attribName);
        }

        //Set a uniform int on this shader
        public void SetInt(string name, int data)
        {
            GL.UseProgram(Handle);
            GL.Uniform1(_uniformLocation[name], data);
        }

        //Set a uniform float on this shader
        public void SetFloat(string name, float data)
        {
            GL.UseProgram(Handle);
            GL.Uniform1(_uniformLocation[name], data);
        }

        //Set a uniform Matrix4 on this shader
        public void SetMatrix4(string name, Matrix4 data)
        {
            GL.UseProgram(Handle);
            GL.UniformMatrix4(_uniformLocation[name], true, ref data);
        }

        //Set a uniform Vector3 on this shader
        public void SetVector3(string name, Vector3 data)
        {
            GL.UseProgram(Handle);
            GL.Uniform3(_uniformLocation[name], data);
        }
    }
}