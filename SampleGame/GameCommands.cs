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
                DevConsole.AddVariable(variable.Name, variable.Description + " Host only.", () => variable.Get(player), value => multiplayer.SetServerVariable(variable, value));
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

            DevConsole.AddCommand("host", "Hosts a game other computers on this network can join: host [port].",
                args => multiplayer.HostLocal(args.Length > 0 && int.TryParse(args[0], out int port) ? port : UdpTransport.DefaultPort));
            DevConsole.AddCommand("connect", "Joins a game hosted on this network: connect <address> [port].", args =>
            {
                if (args.Length == 0)
                {
                    DevConsole.Error("Usage: connect <address> [port], for example: connect 192.168.1.20");
                    return;
                }

                multiplayer.JoinLocal(args[0], args.Length > 1 && int.TryParse(args[1], out int port) ? port : UdpTransport.DefaultPort);
            });
            DevConsole.AddCommand("steam_host", "Hosts a Steam lobby that friends can join.", _ => multiplayer.HostSteam());
            DevConsole.AddCommand("steam_join", "Joins any open Steam lobby of this game.", _ => multiplayer.FindSteam());
            DevConsole.AddCommand("disconnect", "Leaves the current game.", _ => multiplayer.Leave());
        }
    }
}
