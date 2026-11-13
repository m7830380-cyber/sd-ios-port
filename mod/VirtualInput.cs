using System;
using SdShim;
using UnityEngine;

namespace SdIOS
{
    public static class VirtualInput
    {
        public static Vector2 MoveStick;
        public static Vector2 LookDelta;
        public static Vector2 MousePosition;
        public static Vector2 ScrollDelta;

        public static void Init()
        {
            VInput.Virtual = true;
        }

        public static void CommitFrame()
        {
            VInput.VHorizontalAxis = MoveStick.x;
            VInput.VVerticalAxis = MoveStick.y;

            // Commit look delta to mouse delta & virtual axes
            VInput.VMouseDelta += LookDelta;
            VInput.VScroll += ScrollDelta;
            LookDelta = Vector2.zero;
            ScrollDelta = Vector2.zero;

            // Commit mouse pointer position
            VInput.VMousePosition = new Vector3(MousePosition.x, MousePosition.y, 0f);

            // Drive standard WASD keys from thumbstick for any systems that poll keys directly
            VInput.SetKey(KeyCode.W, MoveStick.y > 0.3f);
            VInput.SetKey(KeyCode.S, MoveStick.y < -0.3f);
            VInput.SetKey(KeyCode.D, MoveStick.x > 0.3f);
            VInput.SetKey(KeyCode.A, MoveStick.x < -0.3f);
        }
    }
}
