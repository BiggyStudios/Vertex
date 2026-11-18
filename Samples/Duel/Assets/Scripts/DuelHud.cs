using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.UI;

namespace Duel
{
    /// <summary>
    /// The writing on the screen: how connecting is going (Enter finds a match), your health, and the score. Put it on
    /// the HUD, and point its slots at the HUD's texts.
    /// </summary>
    public class DuelHud : Component
    {
        public UIText? StatusText { get; set; }
        public UIText? HealthText { get; set; }
        public UIText? ScoreText { get; set; }

        private int _mine;
        private int _theirs;

        protected override void Update()
        {
            NetworkManager? network = NetworkManager.Instance;
            if (InputSystem.GetKeyDown(Key.Enter) && !Network.InSession) network?.QuickMatch();

            if (StatusText != null)
            {
                string status = network?.Status ?? "Offline";
                StatusText.Text = status == "Offline" ? "Press Enter to find a match" : status;
            }

            if (HealthText != null && network?.LocalPlayer?.GetComponent<Health>() is { } health) HealthText.Text = $"Health {health.Current:0}";
            if (ScoreText != null) ScoreText.Text = $"{_mine} - {_theirs}";
        }

        protected override void OnNetworkEvent(NetworkEvent networkEvent)
        {
            if (networkEvent.Name != "Scored") return;

            // Who scored is their player: this machine's own, or the other one.
            if (networkEvent.Values.ElementAtOrDefault(0) is GameObject scorer && scorer == NetworkManager.Instance?.LocalPlayer) _mine++;
            else _theirs++;
        }
    }
}
