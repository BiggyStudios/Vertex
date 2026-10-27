using Vertex;
using Vertex.Networking;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>The game's console commands and variables. Type help in the console (` key) for the full list.</summary>
    internal static class GameCommands
    {
        public static void Register(Player player, MultiplayerManager multiplayer)
        {
            Settings settings = Settings.Current;

            void Change(Action change)
            {
                change();
                settings.Apply(player);
                settings.Save();
            }

            DevConsole.AddVariable("sensitivity", "Mouse sensitivity, on Source's scale.", () => settings.Sensitivity, v => Change(() => settings.Sensitivity = v));
            DevConsole.AddVariable("fov_desired", "Horizontal field of view in degrees, as on a 4:3 screen.", () => settings.FieldOfView, v => Change(() => settings.FieldOfView = v));
            DevConsole.AddVariable("volume", "Sound volume, from 0 to 1.", () => settings.Volume, v => Change(() => settings.Volume = v));
            DevConsole.AddVariable("ui_scale", "Interface size; 1 is normal.", () => settings.UiScale, v => Change(() => settings.UiScale = v));
            DevConsole.AddVariable("mat_bloomscale", "How strongly glowing things bloom; 0 turns it off.", () => Scene.Active.Lighting.Bloom, v => Scene.Active.Lighting.Bloom = MathF.Max(v, 0f));

            foreach (ServerVariables.Variable variable in ServerVariables.All)
            {
                DevConsole.AddVariable(variable.Name, variable.Description + " Only the host can change it, in a private room or playing alone.", () => variable.Get(player), value => multiplayer.SetServerVariable(variable, value));
            }

            DevConsole.AddCommand("say", "Sends a chat message.", args => multiplayer.SendChat(string.Join(' ', args)));
            DevConsole.AddCommand("kill", "Kills your player.", _ => player.Kill());
            DevConsole.AddCommand("noclip", "Flies through everything: WASD where you look, Shift faster, E/Q up and down. Needs sv_cheats 1.", _ =>
            {
                if (!player.CheatsAllowed)
                {
                    DevConsole.Error("Can't use cheat command noclip unless the server has sv_cheats set to 1.");
                    return;
                }

                player.Noclip = !player.Noclip;
                DevConsole.Print(player.Noclip ? "noclip ON" : "noclip OFF");
            });
            DevConsole.AddCommand("bot_add", "Puts a bot where you stand, facing the way you face, to shoot at. It doesn't move or fight back. Needs sv_cheats 1.", _ =>
            {
                if (!player.CheatsAllowed)
                {
                    DevConsole.Error("Can't use cheat command bot_add unless the server has sv_cheats set to 1.");
                    return;
                }

                if (!multiplayer.AddBot())
                {
                    DevConsole.Error("There are as many bots as there can be. bot_kick takes them away.");
                    return;
                }

                DevConsole.Print("Added a bot where you stand. Step out of it and it's solid.");
            });
            DevConsole.AddCommand("bot_kick", "Takes away every bot, for everyone in the room.", _ =>
            {
                int count = multiplayer.KickBots();
                DevConsole.Print(count == 1 ? "Took away 1 bot." : $"Took away {count} bots.");
            });
            DevConsole.AddCommand("class", "Changes class: class <name>. With no name, lists the classes.", args =>
            {
                PlayerClass? chosen = args.Length > 0
                    ? PlayerClass.All.FirstOrDefault(c => c.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase))
                    : null;

                if (chosen == null)
                {
                    if (args.Length > 0) DevConsole.Error($"No class called {args[0]}.");
                    DevConsole.Print($"Classes: {string.Join(", ", PlayerClass.All.Select(c => c.Name.ToLowerInvariant()))}. You're a {player.Class.Name.ToLowerInvariant()}.");
                    return;
                }

                player.SetClass(chosen);
                DevConsole.Print($"You're now a {chosen.Name.ToLowerInvariant()}, with the {chosen.Weapon.Name.ToLowerInvariant()}.");
            });
            DevConsole.AddCommand("status", "Lists the players in the game, and who's hosting.", _ => multiplayer.PrintStatus());

            DevConsole.AddCommand("host", "Hosts a room other computers on this network can join: host [port] [password].", args =>
            {
                int port = args.Length > 0 && int.TryParse(args[0], out int typed) ? typed : UdpTransport.DefaultPort;
                multiplayer.HostLocal(port, new RoomSettings("Local game", args.Length > 1 ? args[1] : ""));
            });
            DevConsole.AddCommand("connect", "Joins a room hosted on this network: connect <address> [port] [password].", args =>
            {
                if (args.Length == 0)
                {
                    DevConsole.Error("Usage: connect <address> [port] [password], for example: connect 192.168.1.20");
                    return;
                }

                multiplayer.JoinLocal(args[0], args.Length > 1 && int.TryParse(args[1], out int port) ? port : UdpTransport.DefaultPort,
                    args.Length > 2 ? args[2] : "");
            });
            DevConsole.AddCommand("steam_host", "Hosts a Steam room that anyone can find and friends can join.", _ => multiplayer.HostSteam());
            DevConsole.AddCommand("steam_join", "Joins any Steam room of this game with space and no password.", _ => multiplayer.FindSteam());
            DevConsole.AddCommand("votekick", "Calls a vote to kick someone from the room: votekick <name>. The host can't be kicked.", args =>
            {
                string wanted = string.Join(' ', args);
                RemotePlayer? target = multiplayer.RemotePlayers.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? multiplayer.RemotePlayers.FirstOrDefault(p => wanted.Length > 0 && p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    DevConsole.Error(wanted.Length == 0 ? "Usage: votekick <name>" : $"Nobody called {wanted} in the room.");
                    return;
                }

                if (!multiplayer.Votes.Call(target.Peer, out string? problem)) DevConsole.Error(problem!);
            });
            DevConsole.AddCommand("vote", "Votes in the vote going on: vote yes, or vote no. F1 and F2 do the same.", args =>
            {
                if (args.Length == 0 || args[0].ToLowerInvariant() is not ("yes" or "no"))
                {
                    DevConsole.Error("Usage: vote yes, or vote no");
                    return;
                }

                multiplayer.Votes.Cast(args[0].Equals("yes", StringComparison.OrdinalIgnoreCase));
            });
            DevConsole.AddCommand("disconnect", "Leaves the current room.", _ => multiplayer.Leave());
        }
    }
}
