using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// The whole console, in eight buttons.
///
/// Every system in the game asks this class what the player is holding, so the
/// eight-button limit is enforced in one place instead of being re-remembered in
/// each script. Keyboard and gamepad both feed the same eight, using the bindings
/// every GBA emulator has used for twenty years: Z is A, X is B, Enter is Start.
///
/// Polled rather than event driven, so it needs no scene object and no lifetime.
/// It reads Keyboard.current and Gamepad.current directly because this project is
/// set to "Input System Package (New)", where UnityEngine.Input throws.
/// </summary>
public static class GbaInput
{
    public enum Button { Up, Down, Left, Right, A, B, Start, Select }

    /// <summary>True for as long as the button is down.</summary>
    public static bool Held(Button button)
    {
        List<ButtonControl> controls = ControlsFor(button);
        for (int i = 0; i < controls.Count; i++)
            if (controls[i].isPressed)
                return true;

        return false;
    }

    /// <summary>True on the single frame the button goes down.</summary>
    public static bool Pressed(Button button)
    {
        List<ButtonControl> controls = ControlsFor(button);
        for (int i = 0; i < controls.Count; i++)
            if (controls[i].wasPressedThisFrame)
                return true;

        return false;
    }

    /// <summary>True on the single frame the button comes back up.</summary>
    public static bool Released(Button button)
    {
        List<ButtonControl> controls = ControlsFor(button);
        for (int i = 0; i < controls.Count; i++)
            if (controls[i].wasReleasedThisFrame)
                return true;

        return false;
    }

    /// <summary>-1, 0 or 1 from a pair of opposed buttons. There is no analogue stick here.</summary>
    public static float Axis(Button negative, Button positive)
    {
        return (Held(positive) ? 1f : 0f) - (Held(negative) ? 1f : 0f);
    }

    // Reused rather than allocated, because this is called a dozen times a frame and
    // a per-call array would be a per-call trip to the garbage collector.
    private static readonly List<ButtonControl> matched = new List<ButtonControl>(6);

    private static List<ButtonControl> ControlsFor(Button button)
    {
        matched.Clear();

        Keyboard k = Keyboard.current;
        Gamepad g = Gamepad.current;

        switch (button)
        {
            case Button.Up:
                Add(k?.upArrowKey); Add(k?.wKey);
                Add(g?.dpad.up); Add(g?.leftStick.up);
                break;
            case Button.Down:
                Add(k?.downArrowKey); Add(k?.sKey);
                Add(g?.dpad.down); Add(g?.leftStick.down);
                break;
            case Button.Left:
                Add(k?.leftArrowKey); Add(k?.aKey);
                Add(g?.dpad.left); Add(g?.leftStick.left);
                break;
            case Button.Right:
                Add(k?.rightArrowKey); Add(k?.dKey);
                Add(g?.dpad.right); Add(g?.leftStick.right);
                break;
            case Button.A:
                Add(k?.zKey); Add(k?.spaceKey);
                Add(g?.buttonSouth); Add(g?.rightTrigger);
                break;
            case Button.B:
                Add(k?.xKey); Add(k?.leftCtrlKey);
                Add(g?.buttonEast); Add(g?.leftTrigger);
                break;
            case Button.Start:
                Add(k?.enterKey); Add(k?.pKey);
                Add(g?.startButton);
                break;
            case Button.Select:
                Add(k?.rightShiftKey); Add(k?.backspaceKey);
                Add(g?.selectButton);
                break;
        }

        return matched;
    }

    private static void Add(ButtonControl control)
    {
        if (control != null)
            matched.Add(control);
    }
}
