using System.Numerics;

using Vertex;
using Vertex.Input;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.UI;

// A whole game in one file, made only in code: roll the ball with WASD and pick up every coin before time runs out.
new CoinGame().Run("Coin Grab");

/// <summary>Builds the world when it starts, and keeps score and time.</summary>
public class CoinGame : Game
{
    public const int CoinCount = 10;
    public const float TimeLimit = 60f;

    public static int Score { get; set; }
    public float TimeLeft { get; private set; } = TimeLimit;

    public override void Load()
    {
        Score = 0;

        // Light and ground.
        var sun = new GameObject("Sun");
        sun.AddComponent<Light>();
        sun.Transform.EulerAngles = new Vector3(-50f, 30f, 0f);
        GameObject.CreatePlane(Vector3.Zero, size: 30f, color: new Color(0.35f, 0.55f, 0.35f), name: "Ground");

        // Low walls round the edge, so the ball stays in.
        for (int side = 0; side < 4; side++)
        {
            float angle = side * MathF.PI / 2f;
            var along = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            GameObject wall = GameObject.CreateCube(along * 15f + new Vector3(0f, 0.5f, 0f), new Color(0.5f, 0.45f, 0.4f), "Wall");
            wall.Transform.Scale = side % 2 == 0 ? new Vector3(0.5f, 1f, 30f) : new Vector3(30f, 1f, 0.5f);
        }

        // The player: a ball that physics rolls about.
        GameObject ball = GameObject.CreateSphere(new Vector3(0f, 1f, 0f), new Color(0.25f, 0.5f, 1f), "Ball");
        ball.AddComponent<Rigidbody>(body => body.Mass = 1f);
        ball.AddComponent<Roller>();

        // A camera that follows it.
        var camera = new GameObject("Camera");
        camera.AddComponent<Camera>();
        camera.AddComponent<Follow>(follow => follow.Target = ball);

        for (int i = 0; i < CoinCount; i++) Coin.Make(RandomSpot());
    }

    public override void Update()
    {
        if (Score < CoinCount) TimeLeft = MathF.Max(TimeLeft - Time.DeltaTime, 0f);

        string text = Score == CoinCount ? $"All {CoinCount} coins with {TimeLeft:0} seconds to spare!"
            : TimeLeft <= 0f ? $"Out of time: {Score} of {CoinCount} coins. Press R to play again."
            : $"Coins {Score}/{CoinCount}   Time {TimeLeft:0}";
        Canvas.DrawText(Font.Default, text, new Vector2(24f, 20f), Color.White, 30f);

        if (InputSystem.GetKeyDown(Key.R))
        {
            // Again from the start: a fresh scene, built again.
            Scene old = Scene.Active;
            Scene.Active = new Scene();
            old.Unload();
            TimeLeft = TimeLimit;
            Load();
        }
    }

    public static Vector3 RandomSpot() => new(Random.Shared.NextSingle() * 24f - 12f, 0.6f, Random.Shared.NextSingle() * 24f - 12f);
}

/// <summary>Pushes the ball the way WASD points, as seen from the camera.</summary>
public class Roller : Component
{
    public float Push { get; set; } = 12f;

    protected override void FixedUpdate()
    {
        float forward = (InputSystem.GetKey(Key.W) ? 1f : 0f) - (InputSystem.GetKey(Key.S) ? 1f : 0f);
        float right = (InputSystem.GetKey(Key.D) ? 1f : 0f) - (InputSystem.GetKey(Key.A) ? 1f : 0f);
        GetComponent<Rigidbody>()?.AddForce(new Vector3(right, 0f, -forward) * Push);
    }
}

/// <summary>Keeps the camera behind and above its target, looking at it.</summary>
public class Follow : Component
{
    public GameObject? Target { get; set; }
    public Vector3 Offset { get; set; } = new(0f, 8f, 10f);

    protected override void Update()
    {
        if (Target == null) return;
        Transform.Position = Vector3.Lerp(Transform.Position, Target.Transform.Position + Offset, MathF.Min(Time.DeltaTime * 5f, 1f));
        Transform.LookAt(Target.Transform.Position);
    }
}

/// <summary>A spinning coin. Touching it scores, and it's gone.</summary>
public class Coin : Component
{
    public static GameObject Make(Vector3 position)
    {
        GameObject coin = GameObject.CreateSphere(position, new Color(1f, 0.8f, 0.2f), "Coin");
        coin.Transform.Scale = new Vector3(0.6f, 0.6f, 0.15f);
        coin.GetComponent<SphereCollider>()!.IsTrigger = true;
        coin.AddComponent<Coin>();
        return coin;
    }

    protected override void Update() => Transform.EulerAngles += new Vector3(0f, 120f * Time.DeltaTime, 0f);

    protected override void OnTriggerEnter(Collider other)
    {
        if (other.GameObject.Name != "Ball") return;
        CoinGame.Score++;
        GameObject.Destroy();
    }
}
