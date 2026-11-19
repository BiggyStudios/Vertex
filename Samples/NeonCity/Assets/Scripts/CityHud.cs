using System.Numerics;

using Vertex;
using Vertex.Input;
using Vertex.UI;

namespace NeonCity
{
    /// <summary>
    /// The keys, in a line along the bottom, fading out after a while (H brings them back); and F turns the volumetric fog
    /// on and off, to see what it adds.
    /// </summary>
    public class CityHud : Component
    {
        private float _shownFor;

        protected override void Update()
        {
            if (InputSystem.GetKeyDown(Key.F)) Scene.Active.Lighting.VolumetricFog = !Scene.Active.Lighting.VolumetricFog;
            if (InputSystem.GetKeyDown(Key.H)) _shownFor = 0f;
            _shownFor += Time.DeltaTime;

            float alpha = 1f - Math.Clamp((_shownFor - 12f) / 2f, 0f, 1f);
            if (alpha <= 0f) return;

            string fog = Scene.Active.Lighting.VolumetricFog ? "on" : "off";
            string text = $"Click to look round    WASD walk    Shift hurry    Space jump    F fog ({fog})    Esc free the mouse";
            float size = 18f;
            Vector2 at = new(24f, Canvas.Height - 44f);
            Canvas.DrawText(Font.Default, text, at + new Vector2(1.5f, 1.5f), new Color(0f, 0f, 0f, 0.6f * alpha), size);
            Canvas.DrawText(Font.Default, text, at, new Color(0.9f, 0.92f, 1f, 0.85f * alpha), size);
        }
    }
}
