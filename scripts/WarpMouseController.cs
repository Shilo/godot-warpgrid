using Godot;
using System;

namespace WarpGrid;

[GlobalClass]
public partial class WarpMouseController : Node2D
{
    [Export] public float ForceStrength = 15.0f;
    [Export] public float ImpulseStrength = 150.0f; // Phase 6.1: 250 -> 150; 250 blew out brightness, 150 still triggers global ripple through normalized Gaussian
    [Export] public float CursorRadius = 400.0f;    // Bubble wide enough that right-click repulsion + left-click well span several cells

    private WarpEffector _effector;
    private bool _triggerImpulse = false;

    public override void _Ready()
    {
        // Internal effector managed by the controller
        _effector = new WarpEffector
        {
            Name = "MouseEffector",
            Radius = CursorRadius,
            Visible = false
        };
        AddChild(_effector);
    }

    public override void _Input(InputEvent @event)
    {
        // Capture Middle Click for a single-frame impulse
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

        bool leftPressed  = Input.IsMouseButtonPressed(MouseButton.Left);
        bool rightPressed = Input.IsMouseButtonPressed(MouseButton.Right);

        // Priority logic: Impulse -> Left (Positive) -> Right (Negative)
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
