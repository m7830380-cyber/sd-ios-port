using System;
using UnityEngine;

namespace SdShim
{
    /// <summary>
    /// Complete virtual replacement for UnityEngine.Input.
    /// Rewired, legacy UI, and game assemblies are redirected here by SdPatcher.
    /// </summary>
    public static class VInput
    {
        public static bool Virtual;

        // ---- Virtual Mouse -------------------------------------------------------------------
        public static Vector3 VMousePosition;
        public static Vector2 VScroll;
        static Vector2 scrollThisFrame;
        public static Vector2 VMouseDelta;
        static Vector2 mouseDeltaThisFrame;
        public static string VInputString = "";
        static string inputStringThisFrame = "";
        static readonly bool[] mouseWant = new bool[3];
        static readonly bool[] mouseCur = new bool[3];
        static readonly bool[] mousePrev = new bool[3];
        static readonly int[] mouseTapFrames = new int[3];

        public static void SetMouseButton(int b, bool held) { if (b >= 0 && b < 3) mouseWant[b] = held; }
        public static void TapMouseButton(int b) { if (b >= 0 && b < 3) mouseTapFrames[b] = 1; }
        public static bool IsMouseHeld(int b) => b >= 0 && b < 3 && mouseCur[b];

        // ---- Virtual Keys & Axes --------------------------------------------------------------
        const int MaxKey = 512;
        static readonly bool[] keyWant = new bool[MaxKey];
        static readonly bool[] keyCur = new bool[MaxKey];
        static readonly bool[] keyPrev = new bool[MaxKey];
        static readonly int[] keyTapFrames = new int[MaxKey];
        static bool anyVirtualKey, anyVirtualKeyDown;

        public static float VHorizontalAxis;
        public static float VVerticalAxis;

        public static void SetKey(KeyCode k, bool held)
        {
            int i = (int)k;
            if (i <= 0 || i >= MaxKey) return;
            keyWant[i] = held;
        }

        public static void TapKey(KeyCode k, int frames = 1)
        {
            int i = (int)k;
            if (i <= 0 || i >= MaxKey) return;
            keyTapFrames[i] = Math.Max(keyTapFrames[i], frames);
        }

        public static bool IsVirtualKeyHeld(KeyCode k) { int i = (int)k; return i > 0 && i < MaxKey && keyWant[i]; }

        public static int Frame { get; private set; }

        public static void BeginFrame()
        {
            Frame++;
            for (int b = 0; b < 3; b++)
            {
                mousePrev[b] = mouseCur[b];
                bool tap = mouseTapFrames[b] > 0;
                if (tap) mouseTapFrames[b]--;
                mouseCur[b] = mouseWant[b] || tap;
            }
            anyVirtualKey = anyVirtualKeyDown = false;
            for (int i = 1; i < MaxKey; i++)
            {
                keyPrev[i] = keyCur[i];
                bool tap = keyTapFrames[i] > 0;
                if (tap) keyTapFrames[i]--;
                keyCur[i] = keyWant[i] || tap;
                anyVirtualKey |= keyCur[i];
                anyVirtualKeyDown |= keyCur[i] && !keyPrev[i];
            }
            scrollThisFrame = VScroll;
            VScroll = Vector2.zero;
            mouseDeltaThisFrame = VMouseDelta;
            VMouseDelta = Vector2.zero;
            inputStringThisFrame = VInputString;
            VInputString = "";
        }

        static bool VKey(int i) => i > 0 && i < MaxKey && keyCur[i];
        static bool VKeyDown(int i) => i > 0 && i < MaxKey && keyCur[i] && !keyPrev[i];
        static bool VKeyUp(int i) => i > 0 && i < MaxKey && !keyCur[i] && keyPrev[i];

        static int MouseIndex(KeyCode k) => k >= KeyCode.Mouse0 && k <= KeyCode.Mouse2 ? (int)(k - KeyCode.Mouse0) : -1;

        // ---- UnityEngine.Input Surface ---------------------------------------------------------
        public static bool GetKey(KeyCode k)
        {
            if (!Virtual) return Input.GetKey(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return mouseCur[mb] || VKey((int)k);
            return VKey((int)k) || Input.GetKey(k);
        }

        public static bool GetKeyDown(KeyCode k)
        {
            if (!Virtual) return Input.GetKeyDown(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return (mouseCur[mb] && !mousePrev[mb]) || VKeyDown((int)k);
            return VKeyDown((int)k) || Input.GetKeyDown(k);
        }

        public static bool GetKeyUp(KeyCode k)
        {
            if (!Virtual) return Input.GetKeyUp(k);
            int mb = MouseIndex(k);
            if (mb >= 0) return (!mouseCur[mb] && mousePrev[mb]) || VKeyUp((int)k);
            return VKeyUp((int)k) || Input.GetKeyUp(k);
        }

        public static bool GetKey(string name) => Input.GetKey(name);
        public static bool GetKeyDown(string name) => Input.GetKeyDown(name);
        public static bool GetKeyUp(string name) => Input.GetKeyUp(name);

        public static bool GetMouseButton(int b) => Virtual ? b >= 0 && b < 3 && mouseCur[b] : Input.GetMouseButton(b);
        public static bool GetMouseButtonDown(int b) => Virtual ? b >= 0 && b < 3 && mouseCur[b] && !mousePrev[b] : Input.GetMouseButtonDown(b);
        public static bool GetMouseButtonUp(int b) => Virtual ? b >= 0 && b < 3 && !mouseCur[b] && mousePrev[b] : Input.GetMouseButtonUp(b);

        public static Vector3 mousePosition => Virtual ? VMousePosition : Input.mousePosition;
        public static Vector2 mouseScrollDelta => Virtual ? scrollThisFrame : Input.mouseScrollDelta;
        public static bool mousePresent => Virtual || Input.mousePresent;

        // Suppress native touch events so Unity UI / game logic doesn't duplicate virtual mouse events
        public static int touchCount => Virtual ? 0 : Input.touchCount;
        public static Touch GetTouch(int i) => Input.GetTouch(i);
        public static bool touchSupported => !Virtual && Input.touchSupported;

        public static bool anyKey => Virtual ? anyVirtualKey || mouseCur[0] || mouseCur[1] || mouseCur[2] || Input.anyKey : Input.anyKey;
        public static bool anyKeyDown => Virtual ? anyVirtualKeyDown || GetMouseButtonDown(0) || GetMouseButtonDown(1) || Input.anyKeyDown : Input.anyKeyDown;

        public static float GetAxis(string axis) => Virtual ? VirtualAxis(axis) : SafeAxis(axis, false);
        public static float GetAxisRaw(string axis) => Virtual ? VirtualAxis(axis) : SafeAxis(axis, true);

        static float VirtualAxis(string axis)
        {
            switch (axis)
            {
                case "Horizontal":
                    if (Mathf.Abs(VHorizontalAxis) > 0.01f) return VHorizontalAxis;
                    if (VKey((int)KeyCode.D) || VKey((int)KeyCode.RightArrow)) return 1f;
                    if (VKey((int)KeyCode.A) || VKey((int)KeyCode.LeftArrow)) return -1f;
                    break;
                case "Vertical":
                    if (Mathf.Abs(VVerticalAxis) > 0.01f) return VVerticalAxis;
                    if (VKey((int)KeyCode.W) || VKey((int)KeyCode.UpArrow)) return 1f;
                    if (VKey((int)KeyCode.S) || VKey((int)KeyCode.DownArrow)) return -1f;
                    break;
                case "Mouse ScrollWheel": return scrollThisFrame.y * 0.1f;
                case "Mouse X": return mouseDeltaThisFrame.x * 0.1f;
                case "Mouse Y": return mouseDeltaThisFrame.y * 0.1f;
            }
            return SafeAxis(axis, false);
        }

        static float SafeAxis(string axis, bool raw)
        {
            try { return raw ? Input.GetAxisRaw(axis) : Input.GetAxis(axis); }
            catch (Exception) { return 0f; }
        }

        public static bool GetButton(string b) { try { return Input.GetButton(b); } catch (Exception) { return false; } }
        public static bool GetButtonDown(string b) { try { return Input.GetButtonDown(b); } catch (Exception) { return false; } }
        public static bool GetButtonUp(string b) { try { return Input.GetButtonUp(b); } catch (Exception) { return false; } }

        public static string inputString => Virtual ? inputStringThisFrame + Input.inputString : Input.inputString;
        public static string compositionString => Input.compositionString;
        public static IMECompositionMode imeCompositionMode { get => Input.imeCompositionMode; set => Input.imeCompositionMode = value; }
        public static Vector2 compositionCursorPos { get => Input.compositionCursorPos; set => Input.compositionCursorPos = value; }
    }
}
