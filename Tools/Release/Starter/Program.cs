using System.Numerics;

using Vertex;
using Vertex.Rendering;

// A game made in code: a window with a cube turning on a field. Change Load to make it your own. The Coin Grab
// tutorial starts from here: https://biggystudios.github.io/Vertex/tutorial-code-only.html
new MyGame().Run("My Game");

public class MyGame : Game
{
    public override void Load()
    {
        var sun = new GameObject("Sun");
        sun.AddComponent<Light>();
        sun.Transform.EulerAngles = new Vector3(-50f, 30f, 0f);

        GameObject.CreatePlane(Vector3.Zero, size: 20f, color: new Color(0.35f, 0.55f, 0.35f), name: "Ground");
        GameObject cube = GameObject.CreateCube(new Vector3(0f, 1f, 0f), new Color(0.25f, 0.5f, 1f), "Cube");
        cube.AddComponent<Spin>();

        var camera = new GameObject("Camera");
        camera.AddComponent<Camera>();
        camera.Transform.Position = new Vector3(0f, 3f, 7f);
        camera.Transform.EulerAngles = new Vector3(-20f, 0f, 0f);
    }
}

/// <summary>Turns its object round and round.</summary>
public class Spin : Component
{
    /// <summary>How fast, in degrees a second.</summary>
    public float Speed { get; set; } = 90f;

    protected override void Update() => Transform.EulerAngles += new Vector3(0f, Speed * Time.DeltaTime, 0f);
}
