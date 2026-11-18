using Vertex.Graphs;
using Vertex.Physics;

namespace Vertex.Scripting
{
    /// <summary>
    /// Runs a visual script (a .vscript file, made in the editor) on its object: its On Start nodes when the object first
    /// runs, On Update every frame, key events when keys go down, and On Collision and On Trigger when it's hit. Each
    /// object running the same script has its own variables.
    /// <para>
    /// A node that goes wrong is reported in the console, and the script stops (it's switched off) rather than going
    /// wrong again every frame.
    /// </para>
    /// </summary>
    public sealed class VisualScript : Component
    {
        /// <summary>The script to run: a .vscript file.</summary>
        [Tooltip("The visual script to run: a .vscript file, made in the editor")]
        public Graph? Script { get; set; }

        /// <summary>
        /// This object's own starting values for some of the script's variables, in place of the script's: set in the
        /// inspector, under the script. The rest start as the script says.
        /// </summary>
        [HideInInspector]
        public List<ScriptVariableValue> Variables { get; set; } = new();

        private ScriptInstance? _instance;

        /// <summary>The script as it's running on this object (its variables), once it's started.</summary>
        public ScriptInstance? Instance => _instance;

        /// <summary>
        /// A variable's value on this object: as it is now while the script runs, otherwise what it starts as. Null if the
        /// script hasn't one called <paramref name="name"/>.
        /// </summary>
        public object? GetVariable(string name)
        {
            if (_instance != null) return _instance.Variables.GetValueOrDefault(name);
            if (Script?.Variable(name) is not { } variable) return null;
            return Variables.FirstOrDefault(v => v.Name == name) is { } own ? own.Get(variable.Type) : variable.Value;
        }

        /// <summary>
        /// Sets a variable on this object, made into its type (a number stays a number): straight away while the script
        /// runs, otherwise as what it starts as. For code to tell a script something: <c>script.SetVariable("Speed", 8f)</c>.
        /// </summary>
        public void SetVariable(string name, object? value)
        {
            if (Script?.Variable(name) is not { } variable) throw new InvalidOperationException($"{Script?.Name ?? "The script"} has no variable called {name}");
            if (_instance != null)
            {
                _instance.Variables[name] = variable.Type.Convert(value);
                return;
            }

            if (Variables.FirstOrDefault(v => v.Name == name) is { } own) own.Set(variable.Type, value);
            else Variables.Add(new ScriptVariableValue(name, variable.Type, value));
        }

        protected internal override void Start()
        {
            if (Script == null) return;
            if (Script.Kind != ScriptNodes.Kind)
            {
                Log.Warning($"{GameObject.Name}: {Script.Name} isn't a visual script");
                return;
            }

            _instance = new ScriptInstance(Script, GameObject);

            // This object's own starting values, for variables the script still has.
            foreach (ScriptVariableValue own in Variables)
            {
                if (Script.Variable(own.Name) is { } variable) _instance.Variables[own.Name] = own.Get(variable.Type);
            }

            Fire("Start");
        }

        protected internal override void Update()
        {
            if (_instance == null || !Enabled) return;
            try
            {
                _instance.RunWaiting();
            }
            catch (ScriptException exception)
            {
                Stopped(exception);
                return;
            }

            Fire("Update");
        }

        protected internal override void OnCollisionEnter(Collision collision) => Fire("Collision", ("Other", collision.GameObject));

        protected internal override void OnTriggerEnter(Collider other) => Fire("Trigger", ("Other", other.GameObject));

        /// <summary>Starts the script's nodes for an event, if it's running.</summary>
        public void Fire(string name, params (string Port, object? Value)[] outputs)
        {
            if (_instance == null || !Enabled) return;

            try
            {
                _instance.Fire(name, outputs);
            }
            catch (ScriptException exception)
            {
                Stopped(exception);
            }
        }

        /// <summary>Reports a node going wrong, and switches the script off rather than going wrong again every frame.</summary>
        private void Stopped(ScriptException exception)
        {
            _instance!.Graph.Failed(exception.Node.Id, exception.InnerException?.Message ?? exception.Message);
            Log.Error($"Visual script {_instance.Graph.Name} on {GameObject.Name} stopped: {exception.Message}");
            Enabled = false;
        }
    }
}
