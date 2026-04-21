using Godot;
using System;

namespace WarpGrid;

[GlobalClass, Tool]
public partial class WarpMouseController : Node2D
{
    [Export] public float ForceStrength   = 2500.0f;  // Arcade-snappy brush: held input keeps feeding the CPU solver each tick.
    [Export] public float ImpulseStrength = 8000.0f;  // Middle-click punch for a single burst in the mass-spring grid.
    [Export] public float CursorRadius    = 50.0f; // Broad cursor brush so the deformation reads like a Geometry Wars wake.
    // Hover preview for the editor viewport: applies ForceStrength every tick at the cursor
    // so tuning tension and damping is possible without entering Play mode. Turn it off if
    // the live deformation is distracting.
    [Export] public bool  EditorPreview   = true;
    // Editor input is typically "held-static" (user parks the cursor and holds Ctrl while
    // tweaking the Inspector), which pumps energy every tick. Scale the modifier force down
    // so the grid does not saturate before the user lets go.
    [Export] public float EditorForceMultiplier = 0.2f;

    private WarpEffector _effector;
    private bool _triggerImpulse = false;

    public override void _Ready()
    {
        _effector = new WarpEffector
        {
            Name = "MouseEffector",
            Radius = CursorRadius,
            Visible = false
        };
        // InternalMode.Front + no Owner: the effector is rebuilt every _Ready, so leaving
        // it runtime-volatile avoids scene-file bloat. Group registration relies on
        // WarpEffector itself being [Tool] so its _Ready fires in-editor.
        AddChild(_effector, false, InternalMode.Front);
    }

    public override void _Input(InputEvent @event)
    {
        // Middle-click impulse — ignored in the editor (no _Input delivery to tool Node2D).
        if (@event is InputEventMouseButton mb &&
            mb.ButtonIndex == MouseButton.Middle &&
            mb.Pressed)
        {
            _triggerImpulse = true;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _effector.GlobalPosition = GetGlobalMousePosition();

        // Editor preview: the Godot editor absorbs mouse clicks, but keyboard state IS
        // forwarded to tool scripts. Use Ctrl/Shift as stand-ins for LMB/RMB so the effector
        // can be driven at full strength from the viewport while tuning in the Inspector.
        //   Ctrl+Shift → negative force (pull, equivalent to RMB)
        //   Ctrl       → positive force (push, equivalent to LMB)
        //   (neither)  → zero-strength hover if EditorPreview is on (tracks cursor, no energy)
        if (Engine.IsEditorHint())
        {
            bool ctrl  = Input.IsKeyPressed(Key.Ctrl);
            bool shift = Input.IsKeyPressed(Key.Shift);
            float editorStrength = ForceStrength * EditorForceMultiplier;
            if (ctrl && shift)      ApplyEffectorState(-editorStrength, WarpBehaviorType.Force);
            else if (ctrl)          ApplyEffectorState( editorStrength, WarpBehaviorType.Force);
            // Hover preview stays visible and tracks the cursor at zero strength. Holding a
            // modifier is the only way to pump energy into the grid; otherwise each tick
            // would add ForceStrength and blow the wave field out to saturation.
            else if (EditorPreview) ApplyEffectorState(0.0f, WarpBehaviorType.Force);
            else                    _effector.Visible = false;
            return;
        }

        bool leftPressed  = Input.IsMouseButtonPressed(MouseButton.Left);
        bool rightPressed = Input.IsMouseButtonPressed(MouseButton.Right);

        // Priority: Impulse -> Left (positive force) -> Right (negative force).
        if (_triggerImpulse)
        {
            ApplyEffectorState(ImpulseStrength, WarpBehaviorType.Impulse);
            _triggerImpulse = false;
        }
        else if (leftPressed)
        {
            ApplyEffectorState(ForceStrength, WarpBehaviorType.Force);
        }
        else if (rightPressed)
        {
            ApplyEffectorState(-ForceStrength, WarpBehaviorType.Force);
        }
        else
        {
            _effector.Visible = false;
        }
    }

    private void ApplyEffectorState(float strength, WarpBehaviorType behavior)
    {
        _effector.Visible = true;
        _effector.Strength = strength;
        _effector.Behavior = behavior;
    }
}
