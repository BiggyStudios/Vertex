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

        private ScriptInstance? _instance;

        /// <summary>The script as it's running on this object (its variables), once it's started.</summary>
        public ScriptInstance? Instance => _instance;

        protected internal override void Start()
        {
            if (Script == null) return;
            if (Script.Kind != ScriptNodes.Kind)
            {
                Log.Warning($"{GameObject.Name}: {Script.Name} isn't a visual script");
                return;
            }

            _instance = new ScriptInstance(Script, GameObject);
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
