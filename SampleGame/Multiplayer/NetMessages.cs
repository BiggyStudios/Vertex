using System.Buffers.Binary;
using System.Numerics;

using Vertex.Networking;

namespace SampleGame
{
    /// <summary>The first byte of every message.</summary>
    internal enum MessageType : byte
    {
        /// <summary>Unreliable, many times a second: <see cref="PlayerState"/>.</summary>
        State = 1,

        /// <summary>Unreliable: a shot's start and end, so others can draw it.</summary>
        Shot,

        /// <summary>Reliable, to the player who was hit: how much damage to take.</summary>
        Hit,

        /// <summary>Reliable, from a player who just died: who killed them (0 for nobody).</summary>
        Died
    }

    /// <summary>Where a player is and what they're doing. <see cref="Time"/> is the sender's clock.</summary>
    internal readonly record struct PlayerState(float Time, Vector3 Position, Vector3 Velocity, float Yaw, float Pitch, byte Health, bool Alive);

    /// <summary>Writes a message into a buffer, usually on the stack: <c>new MessageWriter(stackalloc byte[64], MessageType.Hit)</c>.</summary>
    internal ref struct MessageWriter
    {
        private readonly Span<byte> _buffer;
        private int _length;

        public MessageWriter(Span<byte> buffer, MessageType type)
        {
            _buffer = buffer;
            _buffer[0] = (byte)type;
            _length = 1;
        }

        public readonly ReadOnlySpan<byte> Data => _buffer[.._length];

        public void Write(byte value) => _buffer[_length++] = value;

        public void Write(bool value) => Write(value ? (byte)1 : (byte)0);

        public void Write(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer[_length..], value);
            _length += 4;
        }

        public void Write(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer[_length..], value);
            _length += 8;
        }

        public void Write(Vector3 value)
        {
            Write(value.X);
            Write(value.Y);
            Write(value.Z);
        }

        public void Write(in PlayerState state)
        {
            Write(state.Time);
            Write(state.Position);
            Write(state.Velocity);
            Write(state.Yaw);
            Write(state.Pitch);
            Write(state.Health);
            Write(state.Alive);
        }
    }

    /// <summary>Reads a message written by <see cref="MessageWriter"/>. Reading past the end throws, so a broken message can be skipped.</summary>
    internal ref struct MessageReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public MessageReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            Type = (MessageType)ReadByte();
        }

        public MessageType Type { get; }

        public byte ReadByte() => _data[_position++];

        public bool ReadBool() => ReadByte() != 0;

        public float ReadFloat()
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(_data[_position..]);
            _position += 4;
            return value;
        }

        public ulong ReadULong()
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
            _position += 8;
            return value;
        }

        public Vector3 ReadVector3() => new(ReadFloat(), ReadFloat(), ReadFloat());

        public PlayerState ReadPlayerState() =>
            new(ReadFloat(), ReadVector3(), ReadVector3(), ReadFloat(), ReadFloat(), ReadByte(), ReadBool());

        public PeerId ReadPeer() => new(ReadULong());
    }
}
