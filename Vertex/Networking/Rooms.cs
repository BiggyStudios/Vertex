using System.Net;
using System.Text;

namespace Vertex.Networking
{
    /// <summary>
    /// What a host chooses for their room. A room with a password is private: only those who give it can join. An
    /// empty password makes it public, open to anyone.
    /// </summary>
    public sealed record RoomSettings(string Name, string Password = "", int MaxPlayers = 8)
    {
        public const int MaxNameLength = 40;
        public const int MaxPasswordLength = 64;

        public bool HasPassword => Password.Length > 0;
    }

    /// <summary>A room someone is hosting, as a room search found it. Join it with the transport it came from.</summary>
    public sealed record RoomInfo(string Name, int Players, int MaxPlayers, bool HasPassword)
    {
        /// <summary>The room's Steam lobby, for a room found on Steam; 0 otherwise.</summary>
        public ulong LobbyId { get; init; }

        /// <summary>The host's address, for a room found on the local network.</summary>
        public IPEndPoint? EndPoint { get; init; }

        /// <summary>The game version the room's on (see <see cref="Network.GameVersion"/>). Only the same version can join.</summary>
        public string Version { get; init; } = "";

        public bool IsSameVersion => Version == Network.GameVersion;

        /// <summary>Roughly how long a message takes to get to the room and back, in milliseconds, if it could be told.</summary>
        public int? Ping { get; init; }

        public bool IsFull => Players >= MaxPlayers;
    }

    /// <summary>Why joining a room didn't work.</summary>
    public enum JoinError : byte
    {
        /// <summary>The host never answered: it's gone, or the address is wrong, or a firewall is in the way.</summary>
        NoAnswer,

        WrongPassword,

        Full,

        /// <summary>The host kicked you from this room a moment ago; you can try again once the wait is up.</summary>
        Kicked,

        /// <summary>The host kicked you from this room twice, and won't let you back in while it lasts.</summary>
        Banned,

        /// <summary>The room is on a different version of the game (see <see cref="Network.GameVersion"/>).</summary>
        WrongVersion
    }

    /// <summary>What a player sends to be let into a room: their game version, then the password they were given.</summary>
    internal static class JoinRequest
    {
        public static byte[] Write(ReadOnlySpan<byte> password)
        {
            byte[] version = Encoding.UTF8.GetBytes(Network.GameVersion);
            int length = Math.Min(version.Length, byte.MaxValue);
            var request = new byte[1 + length + password.Length];
            request[0] = (byte)length;
            version.AsSpan(0, length).CopyTo(request.AsSpan(1));
            password.CopyTo(request.AsSpan(1 + length));
            return request;
        }

        /// <summary>False if <paramref name="request"/> isn't one, as from an older version of the game.</summary>
        public static bool Read(ReadOnlySpan<byte> request, out string version, out ReadOnlySpan<byte> password)
        {
            version = "";
            password = default;
            if (request.Length < 1 || request.Length < 1 + request[0]) return false;

            version = Encoding.UTF8.GetString(request.Slice(1, request[0]));
            password = request[(1 + request[0])..];
            return true;
        }
    }
}
