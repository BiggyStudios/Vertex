using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.AI
{
    /// <summary>
    /// Runs a behaviour tree (a .btree file, made in the editor) on its object, every frame: how a character decides what
    /// to do, and does it (see <see cref="BehaviorNodes"/>). Each object running the same tree has its own variables:
    /// what it knows, like the player it's after. A <see cref="NavMeshAgent"/> on the same object walks it where the tree
    /// says.
    /// <para>
    /// A task that goes wrong is reported in the console (and on its node in the editor), and the tree stops rather than
    /// going wrong again every frame. Edits to the tree while the game runs take effect straight away.
    /// </para>
    /// </summary>
    public sealed class BehaviorTree : Component
    {
        /// <summary>The tree to run: a .btree file.</summary>
        [GraphKind(BehaviorNodes.Kind)]
        [Tooltip("The behaviour tree to run: a .btree file, made in the editor")]
        public Graph? Tree { get; set; }

        /// <summary>Whether it starts again from the Root once the tree's finished (it succeeded or failed); otherwise it stops.</summary>
        [Tooltip("Starts again from the Root each time the tree finishes")]
        public bool Loop { get; set; } = true;

        /// <summary>
        /// This object's own starting values for some of the tree's variables, in place of the tree's: set in the
        /// inspector, under the tree. The rest start as the tree says.
        /// </summary>
        [HideInInspector]
        public List<ScriptVariableValue> Variables { get; set; } = new();

        private BehaviorTreeInstance? _instance;

        /// <summary>The tree as it's running on this object (its variables and what it's doing), once it's started.</summary>
        public BehaviorTreeInstance? Instance => _instance;

        /// <summary>How the tree did the last time it was run: running, or how it finished.</summary>
        public BehaviorStatus Status => _instance?.Status ?? BehaviorStatus.Failure;

        /// <summary>
        /// A variable's value on this object: as it is now while the tree runs, otherwise what it starts as. Null if the
        /// tree hasn't one called <paramref name="name"/>.
        /// </summary>
        public object? GetVariable(string name)
        {
            if (_instance != null) return _instance.Variables.GetValueOrDefault(name);
            if (Tree?.Variable(name) is not { } variable) return null;
            return Variables.FirstOrDefault(v => v.Name == name) is { } own ? own.Get(variable.Type) : variable.Value;
        }

        /// <summary>
        /// Sets a variable on this object, made into its type: straight away while the tree runs, otherwise as what it
        /// starts as. For code to tell the tree something: <c>tree.SetVariable("Target", player)</c>.
        /// </summary>
        public void SetVariable(string name, object? value)
        {
            if (Tree?.Variable(name) is not { } variable) throw new InvalidOperationException($"{Tree?.Name ?? "The tree"} has no variable called {name}");
            if (_instance != null)
            {
                _instance.Variables[name] = variable.Type.Convert(value);
                return;
            }

            if (Variables.FirstOrDefault(v => v.Name == name) is { } own) own.Set(variable.Type, value);
            else Variables.Add(new ScriptVariableValue(name, variable.Type, value));
        }

        /// <summary>Stops what the tree's doing and starts it again from the Root, keeping its variables.</summary>
        public void Restart()
        {
            _instance?.Stop();
            Enabled = true;
        }

        protected internal override void Start()
        {
            if (Tree == null) return;
            if (Tree.Kind != BehaviorNodes.Kind)
            {
                Log.Warning($"{GameObject.Name}: {Tree.Name} isn't a behaviour tree");
                return;
            }

            _instance = new BehaviorTreeInstance(Tree, GameObject);
            foreach (ScriptVariableValue own in Variables)
            {
                if (Tree.Variable(own.Name) is { } variable) _instance.Variables[own.Name] = own.Get(variable.Type);
            }
        }

        protected internal override void Update()
        {
            if (_instance == null || !Enabled) return;
            if (!Loop && _instance.Finished) return;

            try
            {
                _instance.Tick(Time.DeltaTime);
            }
            catch (BehaviorException exception)
            {
                _instance.Graph.Failed(exception.Node.Id, exception.InnerException?.Message ?? exception.Message);
                Log.Error($"Behaviour tree {_instance.Graph.Name} on {GameObject.Name} stopped: {exception.Message}");
                _instance.Stop();
                Enabled = false;
            }
        }

        protected internal override void OnDestroy() => _instance?.Stop();
    }
}
