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

        /// <summary>Unreliable: a shot's start, the weapon, and where each pellet ended, so others can draw it and hear the right sound.</summary>
        Shot,

        /// <summary>Reliable, to the player who was hit: how much damage to take, flags (1 headshot, 4 no-scope), the weapon, and the distance in metres.</summary>
        Hit,

        /// <summary>Reliable, from a player who just died: who killed them (0 for nobody), flags (1 headshot, 2 fell, 4 no-scope), the weapon, and the distance in metres.</summary>
        Died,

        /// <summary>Reliable: a chat message.</summary>
        Chat,

        /// <summary>Reliable, from the host: every server variable's name and value (see <see cref="ServerVariables"/>).</summary>
        ServerVariables,

        /// <summary>Reliable: a rocket fired, with the firer's number for it, where it started and which way it's going.</summary>
        Rocket,

        /// <summary>
        /// Reliable: a rocket blew up, decided by whoever sends it (see <see cref="SampleGame.Rocket"/>): whose rocket it
        /// was, their number for it, where, and whether it hit someone directly, and who (killed outright).
        /// </summary>
        RocketExploded,

        /// <summary>Reliable, from the host: each player's colour, as a number into the palette, so no two share one while there are colours to go round.</summary>
        Colors,

        /// <summary>Reliable, to the host: start a vote to kick this player.</summary>
        VoteCall,

        /// <summary>Reliable, to the host: a yes or no in the vote going on.</summary>
        VoteCast,

        /// <summary>
        /// Reliable, from the host: the vote as it stands: who it's to kick, who called it, the yes and no votes, how many
        /// yeses it needs, the seconds left, and how it ended, if it has (see <see cref="VoteKick"/>).
        /// </summary>
        VoteState,

        /// <summary>Unreliable, from the host to everyone, every second: the host's clock, for it to measure their ping by.</summary>
        Ping,

        /// <summary>Unreliable: a <see cref="Ping"/> sent straight back, as it came.</summary>
        Pong,

        /// <summary>Unreliable, from the host, every second: everyone's ping to the host in milliseconds, as it measured them.</summary>
        Pings,

        /// <summary>Reliable, to the host: put a bot here, facing this way (see <see cref="Bot"/>).</summary>
        BotAdd,

        /// <summary>Reliable, to the host: take every bot away.</summary>
        BotKick,

        /// <summary>
        /// Reliable, from the host, whenever one changes: every bot, as its number, where it stands, which way it faces
        /// and its health. The host decides how they're doing; everyone else's are copies.
        /// </summary>
        Bots,

        /// <summary>Reliable, to the host: a bot was hit, as a <see cref="Hit"/> is, after the bot's number.</summary>
        BotHit,

        /// <summary>Reliable, from the host: a bot died, as a <see cref="Died"/> says, after the bot's number.</summary>
        BotDied
    }

    /// <summary>
    /// Where a player is and what they're doing. <see cref="Time"/> is the sender's clock. Each player counts their
    /// own kills and deaths and sends them here, so someone joining late still sees the right scores, along with the
    /// weapon they're holding, so others see it in their hands, and where their grapple is caught, if it is.
    /// </summary>
    internal readonly record struct PlayerState(float Time, Vector3 Position, Vector3 Velocity, float Yaw, float Pitch, byte Health, bool Alive,
        ushort Kills, ushort Deaths, WeaponId Weapon, Vector3? Grapple = null);

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

        public void Write(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_length..], value);
            _length += 2;
        }

        public void Write(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer[_length..], value);
            _length += 4;
        }

        /// <summary>UTF-8, after a one-byte length, so at most 255 bytes; anything longer is cut short.</summary>
        public void Write(string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            int count = Math.Min(bytes.Length, 255);

            // Don't cut a character in half: back up to where the cut-off character starts.
            while (count > 0 && count < bytes.Length && (bytes[count] & 0xC0) == 0x80) count--;

            _buffer[_length] = (byte)count;
            bytes.AsSpan(0, count).CopyTo(_buffer[(_length + 1)..]);
            _length += 1 + count;
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
            Write(state.Kills);
            Write(state.Deaths);
            Write((byte)state.Weapon);
            Write(state.Grapple.HasValue);
            if (state.Grapple is Vector3 grapple) Write(grapple);
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

        /// <summary>Whether everything has been read. Fields added later go at the end, so a message without them still reads.</summary>
        public bool AtEnd => _position >= _data.Length;

        public byte ReadByte() => _data[_position++];

        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUShort()
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
            _position += 2;
            return value;
        }

        public float ReadFloat()
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(_data[_position..]);
            _position += 4;
            return value;
        }

        public string ReadString()
        {
            int length = ReadByte();
            string value = System.Text.Encoding.UTF8.GetString(_data.Slice(_position, length));
            _position += length;
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
            new(ReadFloat(), ReadVector3(), ReadVector3(), ReadFloat(), ReadFloat(), ReadByte(), ReadBool(), ReadUShort(), ReadUShort(), (WeaponId)ReadByte(),
                !AtEnd && ReadBool() ? ReadVector3() : null);

        public PeerId ReadPeer() => new(ReadULong());
    }
}
