using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Assimp;

using AssimpFile = Silk.NET.Assimp.File;
using AssimpScene = Silk.NET.Assimp.Scene;

namespace Vertex.Rendering
{
    /// <summary>
    /// Lets Assimp read model files through <see cref="AssetFiles"/>, so a model (and the files beside it that it names, a
    /// .bin or a .mtl) loads from a built game's <see cref="AssetPack"/> as it does from the disk.
    /// </summary>
    internal static unsafe class ModelFiles
    {
        /// <summary>Imports a model, Assimp asking here for each file it opens.</summary>
        public static AssimpScene* Import(Assimp assimp, string path, uint flags)
        {
            var io = new FileIO
            {
                OpenProc = new PfnFileOpenProc(&Open),
                CloseProc = new PfnFileCloseProc(&Close),
                UserData = null
            };
            return assimp.ImportFileEx(path, flags, &io);
        }

        /// <summary>An open file: its bytes and where reading's got to.</summary>
        private sealed class OpenFile(byte[] data)
        {
            public readonly byte[] Data = data;
            public long Position;
        }

        private static OpenFile Of(AssimpFile* file) => (OpenFile)GCHandle.FromIntPtr((nint)file->UserData).Target!;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static AssimpFile* Open(FileIO* io, byte* name, byte* mode)
        {
            try
            {
                string? path = Marshal.PtrToStringUTF8((nint)name);
                string? how = Marshal.PtrToStringUTF8((nint)mode);
                if (path == null || (how != null && (how.Contains('w') || how.Contains('a') || how.Contains('+'))) || !AssetFiles.Exists(path)) return null;

                var open = new OpenFile(AssetFiles.ReadAllBytes(path));
                var file = (AssimpFile*)NativeMemory.AllocZeroed((nuint)sizeof(AssimpFile));
                file->ReadProc = new PfnFileReadProc(&Read);
                file->WriteProc = new PfnFileReadProc(&Write);
                file->TellProc = new PfnFileTellProc(&Tell);
                file->FileSizeProc = new PfnFileTellProc(&Size);
                file->SeekProc = new PfnFileSeek(&Seek);
                file->FlushProc = new PfnFileFlushProc(&Flush);
                file->UserData = (byte*)GCHandle.ToIntPtr(GCHandle.Alloc(open));
                return file;
            }
            catch (Exception)
            {
                return null;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Close(FileIO* io, AssimpFile* file)
        {
            if (file == null) return;
            GCHandle.FromIntPtr((nint)file->UserData).Free();
            NativeMemory.Free(file);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static nuint Read(AssimpFile* file, byte* buffer, nuint size, nuint count)
        {
            OpenFile open = Of(file);
            if (size == 0 || count == 0) return 0;
            long left = open.Data.Length - open.Position;
            long items = Math.Min((long)count, left / (long)size);
            long bytes = items * (long)size;
            if (bytes > 0) open.Data.AsSpan((int)open.Position, (int)bytes).CopyTo(new Span<byte>(buffer, (int)bytes));
            open.Position += bytes;
            return (nuint)items;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static nuint Write(AssimpFile* file, byte* buffer, nuint size, nuint count) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static nuint Tell(AssimpFile* file) => (nuint)Of(file).Position;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static nuint Size(AssimpFile* file) => (nuint)Of(file).Data.Length;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static Return Seek(AssimpFile* file, nuint offset, Origin origin)
        {
            OpenFile open = Of(file);
            // A step back from where it is comes as a size_t that's wrapped round.
            long by = (long)(ulong)offset;
            long to = origin switch
            {
                Origin.Cur => open.Position + by,
                Origin.End => open.Data.Length + by,
                _ => by
            };

            if (to < 0 || to > open.Data.Length) return Return.Failure;
            open.Position = to;
            return Return.Success;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Flush(AssimpFile* file)
        {
        }
    }
}
