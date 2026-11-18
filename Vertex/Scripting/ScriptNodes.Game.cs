using System.Numerics;

using Vertex.Audio;
using Vertex.Effects;
using Vertex.Graphs;
using Vertex.Input;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Scripting
{
    /// <summary>
    /// The nodes for making a game from visual scripts, a networked shooter included: more events (mouse, timers, custom
    /// and network events), waiting, cooldowns, spawning, raycasts, character movement, sounds, effects, UI text, and
    /// connecting players through Steam or the local network.
    /// </summary>
    public static partial class ScriptNodes
    {
        private static void AddGameNodes(NodeLibrary library)
        {
            void Node(string category, string title, Port[] inputs, Port[] outputs, Action<ScriptRun> run, string description, string keywords = "",
                string? @event = null, Func<ScriptRun, bool>? when = null)
            {
                library.Add(new ScriptNode($"{category}/{title}", title, category, inputs, outputs, run)
                {
                    Description = description,
                    Keywords = keywords,
                    Event = @event,
                    When = when
                });
            }

            // A node in the flow that does something, then carries on.
            void Action(string category, string title, Port[] inputs, Action<ScriptRun> act, string description, string keywords = "", Port[]? outputs = null) =>
                Node(category, title, [FlowIn(), .. inputs], [FlowOut(), .. outputs ?? []], run =>
                {
                    act(run);
                    run.Continue("Then");
                }, description, keywords);

            // An event that starts the flow when one with the same name arrives (its name's in the node's Name setting).
            bool Named(ScriptRun run) => Equals(run.Given("Name"), run.In<string>("Name"));

            Port Number(string name, float value = 0f, string? tooltip = null) => In(name, PortType.Number, value, tooltip);
            Port Vector(string name, Vector3 value = default, string? tooltip = null) => In(name, PortType.Vector, value, tooltip);
            Port Text(string name, string value = "", string? tooltip = null) => In(name, PortType.Text, value, tooltip);
            Port Bool(string name, bool value, string? tooltip = null) => In(name, PortType.Boolean, value, tooltip);

            // ---------------------------------------------------------------- Events

            Node("Events", "On Mouse Pressed", [Setting("Button", PortType.Enum<MouseButton>(), MouseButton.Left)], [FlowOut()], run => run.Continue("Then"),
                "The frame a mouse button goes down: fire!", "click shoot input", @event: "Update", when: run => InputSystem.GetMouseButtonDown(run.In<MouseButton>("Button")));
            Node("Events", "On Mouse Released", [Setting("Button", PortType.Enum<MouseButton>(), MouseButton.Left)], [FlowOut()], run => run.Continue("Then"),
                "The frame a mouse button comes back up.", "click input", @event: "Update", when: run => InputSystem.GetMouseButtonUp(run.In<MouseButton>("Button")));
            Node("Events", "On Interval", [Setting("Seconds", PortType.Number, 1f)], [FlowOut()], run => run.Continue("Then"),
                "Every so many seconds, over and over.", "timer every repeat tick", @event: "Update", when: run =>
                {
                    float waited = (run.State as float? ?? 0f) + Time.DeltaTime;
                    float every = MathF.Max(run.In<float>("Seconds"), 0.01f);
                    bool due = waited >= every;
                    run.State = due ? waited - every : waited;
                    return due;
                });
            Node("Events", "On Event", [Setting("Name", PortType.Text, "Event")], [FlowOut(), Out("Value", PortType.Any)], run => run.Continue("Then"),
                "When Send Event sends one with this name to this object (or every object): one script telling another something happened.",
                "custom message receive", @event: "Custom", when: Named);

            // ---------------------------------------------------------------- Flow

            Node("Flow", "Wait", [FlowIn(), Number("Seconds", 1f)], [FlowOut()], run => run.ContinueAfter("Then", run.In<float>("Seconds")),
                "Carries on after a while: a respawn delay, a reload. Whatever started it carries on meanwhile.", "delay sleep timer later");
            Node("Flow", "Cooldown", [FlowIn(), Number("Seconds", 0.25f, "How long after letting the flow through before it lets it through again")],
                [FlowOut(), FlowOut("Blocked")], run =>
                {
                    float now = Time.TotalTime;
                    if (run.State is float readyAt && now < readyAt)
                    {
                        run.Continue("Blocked");
                        return;
                    }

                    run.State = now + run.In<float>("Seconds");
                    run.Continue("Then");
                },
                "Lets the flow through at most once every so many seconds, and goes to Blocked otherwise: a gun's rate of fire.", "rate limit fire throttle");
            Node("Flow", "Do Once", [FlowIn()], [FlowOut()], run =>
                {
                    if (run.State != null) return;
                    run.State = true;
                    run.Continue("Then");
                },
                "Lets the flow through the first time only.", "once first single");

            // ---------------------------------------------------------------- Actions

            Action("Actions", "Set Rotation", [Target, Vector("Degrees")], run => run.In<GameObject>("Target").Transform.EulerAngles = run.In<Vector3>("Degrees"),
                "Turns an object to face a way, by degrees about X, Y and Z (relative to its parent).", "rotate turn euler angles");
            Action("Actions", "Look At", [Target, Vector("Point")], run => run.In<GameObject>("Target").Transform.LookAt(run.In<Vector3>("Point")),
                "Turns an object so its forward faces a point.", "face aim turn towards");
            Node("Actions", "Spawn Prefab", [FlowIn(), In("Prefab", PortType.Prefab), Vector("Position"), Vector("Degrees")], [FlowOut(), Out("Object", PortType.Object)], run =>
                {
                    var prefab = run.In<Prefab>("Prefab") ?? throw new InvalidOperationException("No prefab chosen");
                    GameObject made = prefab.Instantiate(run.In<Vector3>("Position"));
                    made.Transform.EulerAngles = run.In<Vector3>("Degrees");
                    run.Out("Object", made);
                    run.Continue("Then");
                },
                "Makes a copy of a prefab (on this machine only: see Network Spawn): a bullet, an explosion, a pickup.", "instantiate create make clone");
            Action("Actions", "Set Text", [Target, Text("Text")], run =>
                {
                    var text = run.In<GameObject>("Target").GetComponent<UIText>() ?? throw new InvalidOperationException("The object has no UI Text");
                    text.Text = run.In<string>("Text");
                },
                "Changes what a UI Text says: a score, health, ammo.", "label hud ui string");
            Action("Actions", "Lock Cursor", [Bool("Locked", true)], run => InputSystem.CursorLocked = run.In<bool>("Locked"),
                "Hides the mouse pointer and keeps it in the window, for looking around with the mouse; or frees it again.", "mouse capture hide pointer");
            Action("Actions", "Send Event", [Text("Name", "Event"), In("Value", PortType.Any)], run =>
                {
                    var outputs = new (string, object?)[] { ("Name", run.In<string>("Name")), ("Value", run.In("Value")) };
                    foreach (VisualScript script in Scene.Active.FindComponents<VisualScript>().ToList()) script.Fire("Custom", outputs);
                },
                "Starts On Event nodes with this name in every script in the scene, with a value.", "broadcast message custom");
            Action("Actions", "Send Event To", [Target, Text("Name", "Event"), In("Value", PortType.Any)], run =>
                {
                    var outputs = new (string, object?)[] { ("Name", run.In<string>("Name")), ("Value", run.In("Value")) };
                    foreach (VisualScript script in run.In<GameObject>("Target").GetComponents<VisualScript>().ToList()) script.Fire("Custom", outputs);
                },
                "Starts On Event nodes with this name in the scripts on one object, with a value: tell what was hit that it was.", "message custom notify");
            Action("Actions", "Set Variable On", [Target, Text("Variable", ""), In("Value", PortType.Any)], run =>
                {
                    ScriptInstance script = ScriptOn(run.In<GameObject>("Target"));
                    string name = run.In<string>("Variable");
                    GraphVariable variable = script.Graph.Variable(name) ?? throw new InvalidOperationException($"{script.Self.Name}'s script has no variable called {name}");
                    script.Variables[name] = variable.Type.Convert(run.In("Value"));
                },
                "Changes a variable in another object's visual script.", "other object write remote");
            Node("Actions", "Get Variable On", [Target, Text("Variable", "")], [Out("Value", PortType.Any)],
                run => run.Out("Value", ScriptOn(run.In<GameObject>("Target")).Variables.GetValueOrDefault(run.In<string>("Variable"))),
                "A variable's value in another object's visual script.", "other object read remote");

            // ---------------------------------------------------------------- Physics and movement

            Node("Physics", "Raycast", [FlowIn(), Vector("Origin"), Vector("Direction", -Vector3.UnitZ), Number("Distance", 100f),
                    In("Ignore", PortType.Object, tooltip: "An object the ray passes through: this one if nothing's linked, so a player doesn't shoot themselves")],
                [FlowOut("Hit"), FlowOut("Missed"), Out("Object", PortType.Object), Out("Point", PortType.Vector), Out("Normal", PortType.Vector), Out("Distance", PortType.Number)], run =>
                {
                    Collider? ignore = run.In<GameObject>("Ignore")?.GetComponent<Collider>();
                    bool hit = run.Self.Scene.Physics.Raycast(run.In<Vector3>("Origin"), run.In<Vector3>("Direction"), run.In<float>("Distance"), out RaycastHit found, ignore);
                    run.Out("Object", hit ? found.Collider.GameObject : null);
                    run.Out("Point", hit ? found.Point : Vector3.Zero);
                    run.Out("Normal", hit ? found.Normal : Vector3.Zero);
                    run.Out("Distance", hit ? found.Distance : 0f);
                    run.Continue(hit ? "Hit" : "Missed");
                },
                "Sends a ray from Origin along Direction, and finds the first thing it hits (a shot, a line of sight): goes to Hit or Missed.",
                "shoot trace line hitscan ray cast");
            Node("Physics", "Camera Ray", [], [Out("Origin", PortType.Vector), Out("Direction", PortType.Vector)], run =>
                {
                    Transform from = Camera.Main?.Transform ?? run.Self.Transform;
                    run.Out("Origin", from.WorldPosition);
                    run.Out("Direction", from.Forward);
                },
                "Where the camera is and the way it looks: link to a Raycast to shoot where the player aims.", "aim crosshair view look");
            Action("Physics", "Add Force", [Target, Vector("Force"), Bool("Impulse", false, "All at once (a jump, a knockback), rather than over time")], run =>
                {
                    var body = run.In<GameObject>("Target").GetComponent<Rigidbody>() ?? throw new InvalidOperationException("The object has no Rigidbody");
                    if (run.In<bool>("Impulse")) body.AddImpulse(run.In<Vector3>("Force"));
                    else body.AddForce(run.In<Vector3>("Force"));
                },
                "Pushes an object with a Rigidbody.", "push knockback impulse");
            Action("Physics", "Set Velocity", [Target, Vector("Velocity")], run =>
                {
                    GameObject target = run.In<GameObject>("Target");
                    if (target.GetComponent<Rigidbody>() is { } body) body.Velocity = run.In<Vector3>("Velocity");
                    else if (target.GetComponent<CharacterController>() is { } character) character.Velocity = run.In<Vector3>("Velocity");
                    else throw new InvalidOperationException("The object has no Rigidbody or Character Controller");
                },
                "Sets how fast an object with a Rigidbody or Character Controller moves, and which way.", "speed launch");
            Node("Physics", "Get Velocity", [Target], [Out("Velocity", PortType.Vector)], run =>
                {
                    GameObject target = run.In<GameObject>("Target");
                    run.Out("Velocity", target.GetComponent<Rigidbody>()?.Velocity ?? target.GetComponent<CharacterController>()?.Velocity ?? Vector3.Zero);
                },
                "How fast an object with a Rigidbody or Character Controller is moving.", "speed");
            Node("Physics", "Move Character",
                [FlowIn(), Target, Vector("Move", tooltip: "Metres a second to walk: X sideways, Z forwards and back (negative Z is forwards)"),
                    Bool("Local", true, "Along the object's own facing (turned only about Y), rather than the world's"), Bool("Jump", false),
                    Number("Jump Speed", 5f), Number("Gravity", 20f)],
                [FlowOut(), Out("Grounded", PortType.Boolean)], run =>
                {
                    GameObject target = run.In<GameObject>("Target");
                    var character = target.GetComponent<CharacterController>() ?? throw new InvalidOperationException("The object has no Character Controller");
                    Vector3 move = run.In<Vector3>("Move");
                    move.Y = 0f;
                    if (run.In<bool>("Local"))
                    {
                        float yaw = target.Transform.EulerAngles.Y * (MathF.PI / 180f);
                        move = Vector3.Transform(move, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
                    }

                    float dt = Time.DeltaTime;
                    float rise = character.Velocity.Y;
                    if (character.IsGrounded) rise = run.In<bool>("Jump") ? run.In<float>("Jump Speed") : -1f;
                    else rise -= run.In<float>("Gravity") * dt;

                    character.Velocity = new Vector3(move.X, rise, move.Z);
                    character.Move(dt);
                    run.Out("Grounded", character.IsGrounded);
                    run.Continue("Then");
                },
                "Walks an object with a Character Controller (run it every frame): it slides along walls, climbs steps, falls, and jumps when Jump is true while it's on the ground.",
                "walk run player controller fps wasd");

            // ---------------------------------------------------------------- Objects

            Node("Objects", "Get Rotation", [Target], [Out("Degrees", PortType.Vector)], run => run.Out("Degrees", run.In<GameObject>("Target").Transform.EulerAngles),
                "Which way an object is turned, in degrees about X, Y and Z.", "angles euler");
            Node("Objects", "Get Directions", [Target], [Out("Forward", PortType.Vector), Out("Right", PortType.Vector), Out("Up", PortType.Vector)], run =>
                {
                    Transform transform = run.In<GameObject>("Target").Transform;
                    run.Out("Forward", transform.Forward);
                    run.Out("Right", transform.Right);
                    run.Out("Up", transform.Up);
                },
                "The ways an object's front, right side and top face, in the world.", "forward right up facing axes");
            Node("Objects", "Main Camera", [], [Out("Object", PortType.Object)], run => run.Out("Object", Camera.Main?.GameObject), "The camera the game's seen through.",
                "view");
            Node("Objects", "Get Child", [Target, Text("Name")], [Out("Object", PortType.Object)], run => run.Out("Object", FindUnder(run.In<GameObject>("Target"), run.In<string>("Name"))),
                "The object with this name under another (at any depth), or none: a gun's muzzle, a player's head.", "find descendant");
            Node("Objects", "Get Parent", [Target], [Out("Object", PortType.Object)], run => run.Out("Object", run.In<GameObject>("Target").Transform.Parent?.GameObject),
                "The object another is under, or none.", "up owner");
            Node("Objects", "Is Valid", [In("Object", PortType.Object)], [Out("Valid", PortType.Boolean)], run => run.Out("Valid", run.In<GameObject>("Object") is { IsDestroyed: false }),
                "Whether an object is there: not none, and not destroyed.", "exists null alive");
            Node("Objects", "Same Object", [In("A", PortType.Object), In("B", PortType.Object)], [Out("Result", PortType.Boolean)],
                run => run.Out("Result", run.In<GameObject>("A") == run.In<GameObject>("B")), "Whether A and B are the same object.", "equal is");

            // ---------------------------------------------------------------- Input

            Node("Input", "Mouse Held", [Setting("Button", PortType.Enum<MouseButton>(), MouseButton.Left)], [Out("Held", PortType.Boolean)],
                run => run.Out("Held", InputSystem.GetMouseButton(run.In<MouseButton>("Button"))), "Whether a mouse button is down right now: automatic fire.", "click button");
            Node("Input", "Mouse Movement", [], [Out("X", PortType.Number), Out("Y", PortType.Number)], run =>
                {
                    Vector2 delta = InputSystem.MouseDelta;
                    run.Out("X", delta.X);
                    run.Out("Y", delta.Y);
                },
                "How far the mouse moved this frame, in pixels: turn the player by X and the camera by Y to look around.", "look delta aim");

            // ---------------------------------------------------------------- Math and vectors

            Node("Math", "Absolute", [In("Value", PortType.Number)], [Out("Result", PortType.Number)], run => run.Out("Result", MathF.Abs(run.In<float>("Value"))),
                "The value without its sign.", "abs");
            Node("Math", "Round", [In("Value", PortType.Number)], [Out("Result", PortType.Number)], run => run.Out("Result", MathF.Round(run.In<float>("Value"))),
                "The nearest whole number.", "integer floor");
            Node("Math", "Clamp", [In("Value", PortType.Number), Number("Min", 0f), Number("Max", 1f)], [Out("Result", PortType.Number)],
                run => run.Out("Result", Math.Clamp(run.In<float>("Value"), run.In<float>("Min"), MathF.Max(run.In<float>("Min"), run.In<float>("Max")))),
                "The value kept between Min and Max: health between 0 and 100.", "limit");
            Node("Math", "Modulo", [In("A", PortType.Number), Number("B", 1f)], [Out("Result", PortType.Number)], run =>
                {
                    float b = run.In<float>("B");
                    run.Out("Result", b == 0f ? 0f : run.In<float>("A") - b * MathF.Floor(run.In<float>("A") / b));
                },
                "What's left over when A is divided by B: going round and round.", "remainder wrap %");
            Node("Vectors", "Subtract Vectors", [In("A", PortType.Vector), In("B", PortType.Vector)], [Out("Result", PortType.Vector)],
                run => run.Out("Result", run.In<Vector3>("A") - run.In<Vector3>("B")), "A minus B: the way from B to A.", "minus difference towards");
            Node("Vectors", "Normalize", [In("Vector", PortType.Vector)], [Out("Result", PortType.Vector)], run =>
                {
                    Vector3 v = run.In<Vector3>("Vector");
                    run.Out("Result", v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero);
                },
                "The same direction, a metre long.", "unit direction");
            Node("Vectors", "Length", [In("Vector", PortType.Vector)], [Out("Length", PortType.Number)], run => run.Out("Length", run.In<Vector3>("Vector").Length()),
                "How long a vector is.", "magnitude size");

            // ---------------------------------------------------------------- Sound and effects

            Action("Sound", "Play Sound", [In("Sound", PortType.Sound), Number("Volume", 1f), Bool("In World", true, "From this object, quieter further away; off, it plays everywhere the same")], run =>
                {
                    var clip = run.In<AudioClip>("Sound") ?? throw new InvalidOperationException("No sound chosen");
                    if (run.In<bool>("In World")) Sound.PlayAt(clip, run.Self.Transform.WorldPosition, run.In<float>("Volume"));
                    else Sound.Play(clip, run.In<float>("Volume"));
                },
                "Plays a sound once: a gunshot, a hit, a jump.", "audio sfx");
            Action("Effects", "Play Effect", [Target], run =>
                {
                    GameObject target = run.In<GameObject>("Target");
                    foreach (ParticleEmitter emitter in target.GetComponents<ParticleEmitter>()) emitter.Play();
                    foreach (VisualEffect effect in target.GetComponents<VisualEffect>()) effect.Play();
                },
                "Starts an object's particle effects (Particle System or Visual Effect) from the beginning: a muzzle flash, an explosion.", "particles vfx start");
            Action("Effects", "Emit Particles", [Target, Number("Count", 10f)], run =>
                {
                    foreach (ParticleEmitter emitter in run.In<GameObject>("Target").GetComponents<ParticleEmitter>()) emitter.Emit((int)run.In<float>("Count"));
                },
                "Lets out a burst of particles from an object's particle system at once: sparks where a bullet hit.", "burst spawn");

            // ---------------------------------------------------------------- Networking

            Node("Events", "On Connected", [], [FlowOut()], run => run.Continue("Then"), "When this machine joins (or starts hosting) a network game.",
                "network online session start", @event: "Connected");
            Node("Events", "On Disconnected", [], [FlowOut()], run => run.Continue("Then"), "When this machine leaves a network game, or loses it.",
                "network offline session end", @event: "Disconnected");
            Node("Events", "On Player Joined", [], [FlowOut(), Out("Player", PortType.Text)], run => run.Continue("Then"),
                "When someone else joins the game. Player is their name.", "network peer connect", @event: "Player Joined");
            Node("Events", "On Player Left", [], [FlowOut(), Out("Player", PortType.Text)], run => run.Continue("Then"),
                "When someone else leaves the game. Player is their name.", "network peer disconnect", @event: "Player Left");
            Node("Events", "On Network Event", [Setting("Name", PortType.Text, "Hit")],
                [FlowOut(), Out("From", PortType.Text), Out("A", PortType.Any), Out("B", PortType.Any), Out("C", PortType.Any)], run => run.Continue("Then"),
                "When Send Network Event sends one with this name (to this object, or every script): From is who sent it, A to C what it came with.",
                "network message receive rpc", @event: "Network Event", when: Named);

            Action("Networking", "Quick Match", [], _ => Manager().QuickMatch(), "Joins a game that has room for you, or hosts one if there's none.",
                "connect find join host matchmaking steam");
            Action("Networking", "Host Game", [], _ => Manager().Host(), "Hosts a game for others to join.", "server create lobby room");
            Action("Networking", "Join Game", [Text("Address", "localhost", "The host's address on the local network (localhost for another copy on this PC)")],
                run => Manager().Join(run.In<string>("Address")), "Joins a game hosted on the local network at an address.", "connect lan ip");
            Action("Networking", "Leave Game", [], _ => Manager().Leave(), "Leaves the network game.", "disconnect quit offline");
            Node("Networking", "Network Spawn", [FlowIn(), In("Prefab", PortType.Prefab), Vector("Position"), Vector("Degrees")], [FlowOut(), Out("Object", PortType.Object)], run =>
                {
                    var prefab = run.In<Prefab>("Prefab") ?? throw new InvalidOperationException("No prefab chosen");
                    Vector3 degrees = run.In<Vector3>("Degrees") * (MathF.PI / 180f);
                    GameObject made = NetworkScene.Spawn(prefab, run.In<Vector3>("Position"), Quaternion.CreateFromYawPitchRoll(degrees.Y, degrees.X, degrees.Z));
                    run.Out("Object", made);
                    run.Continue("Then");
                },
                "Makes a copy of a prefab on everyone's machine, owned by this one: a rocket, a grenade. Destroying it destroys it everywhere.",
                "instantiate create networked replicate");
            Action("Networking", "Send Network Event",
                [Text("Name", "Hit"), Target, Setting("To", PortType.Enum<NetworkTarget>(), NetworkTarget.Everyone),
                    new Port("Every Script", PortType.Boolean, false, Linkable: false, Tooltip: "To On Network Event in every script, rather than just the target's"),
                    In("A", PortType.Any), In("B", PortType.Any), In("C", PortType.Any)],
                run => NetworkScene.SendEvent(run.In<string>("Name"), run.In<bool>("Every Script") ? null : run.In<GameObject>("Target"), run.In<NetworkTarget>("To"),
                    run.In("A"), run.In("B"), run.In("C")),
                "Sends an event about an object, with up to three values, to everyone, others, the object's owner or the host: tell a player's owner they were shot.",
                "rpc message network send tell");
            Node("Networking", "Is Mine", [Target], [Out("Mine", PortType.Boolean)],
                run => run.Out("Mine", run.In<GameObject>("Target").GetComponent<NetworkObject>() is not { } networked || networked.IsMine),
                "Whether this machine is in charge of an object: only move your own player, and read keys for it. Always true playing alone.", "owner local authority");
            Node("Networking", "Owner Name", [Target], [Out("Name", PortType.Text)],
                run => run.Out("Name", run.In<GameObject>("Target").GetComponent<NetworkObject>()?.OwnerName ?? NetworkScene.LocalName),
                "The name of the player in charge of an object: who to credit with a kill.", "player who");
            Node("Networking", "Network Info", [],
                [Out("Online", PortType.Boolean), Out("Is Host", PortType.Boolean), Out("Players", PortType.Number), Out("My Name", PortType.Text), Out("Status", PortType.Text)], run =>
                {
                    run.Out("Online", Network.InSession);
                    run.Out("Is Host", Network.IsHost);
                    run.Out("Players", (float)(Network.InSession ? Network.Peers.Count + 1 : 1));
                    run.Out("My Name", NetworkScene.LocalName);
                    run.Out("Status", NetworkManager.Instance?.Status ?? "Offline");
                },
                "Whether this machine's in a network game, whether it's the host, how many are playing, your name, and how connecting is going.",
                "online host count session status");
            Node("Networking", "Local Player", [], [Out("Object", PortType.Object)], run => run.Out("Object", NetworkManager.Instance?.LocalPlayer),
                "This machine's own player object.", "me self mine");
            Node("Networking", "Respawn Point", [], [Out("Position", PortType.Vector), Out("Degrees", PortType.Vector)], run =>
                {
                    SpawnPoint? point = SpawnPoint.FurthestFromOthers(run.Self);
                    run.Out("Position", point?.Transform.WorldPosition ?? Vector3.Zero);
                    run.Out("Degrees", point?.Transform.EulerAngles ?? Vector3.Zero);
                },
                "The spawn point furthest from the other players: where to come back after dying.", "spawn start place");
        }

        private static NetworkManager Manager() =>
            NetworkManager.Instance ?? throw new InvalidOperationException("There's no Network Manager in the scene: add one to an object");

        /// <summary>The visual script running on <paramref name="target"/>.</summary>
        private static ScriptInstance ScriptOn(GameObject target) =>
            target.GetComponents<VisualScript>().Select(s => s.Instance).FirstOrDefault(i => i != null)
            ?? throw new InvalidOperationException($"{target.Name} has no visual script running");

        private static GameObject? FindUnder(GameObject parent, string name)
        {
            foreach (Transform child in parent.Transform.Children)
            {
                if (child.GameObject.Name == name) return child.GameObject;
                if (FindUnder(child.GameObject, name) is { } deeper) return deeper;
            }

            return null;
        }
    }
}
