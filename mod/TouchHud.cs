using System;
using System.Collections.Generic;
using SdShim;
using UnityEngine;

namespace SdIOS
{
    public sealed class TouchHud : MonoBehaviour
    {
        public static float LookSensitivity = 1.0f;
        public static float HudOpacity = 0.85f;
        public static bool AutoSprint = true;

        sealed class Button
        {
            public string Label;
            public KeyCode Key;
            public int MouseBtn = -1;
            public Vector2 Pos;
            public float Radius;
            public bool Held;
            public int FingerId = -1;
        }

        readonly List<Button> m_Buttons = new List<Button>();
        Button m_BtnAttack;
        Button m_BtnAim;
        Button m_BtnInteract;
        Button m_BtnWatch;
        Button m_BtnInventory;
        Button m_BtnCraft;
        Button m_BtnJump;
        Button m_BtnCrouch;
        Button m_BtnDrop;
        Button m_BtnPause;

        // Joystick state
        int m_StickFinger = -1;
        Vector2 m_StickCenter;
        Vector2 m_StickPos;
        float m_StickEdgeTimer;

        // Look state
        int m_LookFinger = -1;
        Vector2 m_LookLastPos;

        // Menu touch state
        int m_MenuFinger = -1;

        Texture2D m_CircleTex;
        Texture2D m_RingTex;
        GUIStyle m_LabelStyle;
        bool m_HideHud;

        void Awake()
        {
            m_CircleTex = MakeCircleTexture(128, false);
            m_RingTex = MakeCircleTexture(128, true);

            m_BtnAttack = new Button { Label = "ATK", MouseBtn = 0 };
            m_BtnAim = new Button { Label = "AIM", MouseBtn = 1 };
            m_BtnInteract = new Button { Label = "USE", Key = KeyCode.E };
            m_BtnWatch = new Button { Label = "WATCH", Key = KeyCode.F };
            m_BtnInventory = new Button { Label = "BAG", Key = KeyCode.Tab };
            m_BtnCraft = new Button { Label = "CRAFT", Key = KeyCode.C };
            m_BtnJump = new Button { Label = "JUMP", Key = KeyCode.Space };
            m_BtnCrouch = new Button { Label = "CROUCH", Key = KeyCode.LeftControl };
            m_BtnDrop = new Button { Label = "DROP", Key = KeyCode.Q };
            m_BtnPause = new Button { Label = "ESC", Key = KeyCode.Escape };

            m_Buttons.Add(m_BtnAttack);
            m_Buttons.Add(m_BtnAim);
            m_Buttons.Add(m_BtnInteract);
            m_Buttons.Add(m_BtnWatch);
            m_Buttons.Add(m_BtnInventory);
            m_Buttons.Add(m_BtnCraft);
            m_Buttons.Add(m_BtnJump);
            m_Buttons.Add(m_BtnCrouch);
            m_Buttons.Add(m_BtnDrop);
            m_Buttons.Add(m_BtnPause);
        }

        void LayoutButtons()
        {
            float w = Screen.width;
            float h = Screen.height;
            float r = h * 0.055f;

            // Insets for iPhone safe area (landscape)
            float safeRight = Screen.safeArea.xMax > 0 ? (w - Screen.safeArea.xMax) : 0f;
            float safeLeft = Screen.safeArea.xMin > 0 ? Screen.safeArea.xMin : 0f;
            float safeBottom = Screen.safeArea.yMin > 0 ? Screen.safeArea.yMin : 0f;

            float rightEdge = w - safeRight - r * 1.2f;
            float bottomEdge = h - safeBottom - r * 1.2f;

            // Primary action cluster (Attack & Aim)
            m_BtnAttack.Pos = new Vector2(rightEdge - r * 2.2f, bottomEdge - r * 1.8f);
            m_BtnAttack.Radius = r * 1.35f;

            m_BtnAim.Pos = new Vector2(rightEdge - r * 4.6f, bottomEdge - r * 1.4f);
            m_BtnAim.Radius = r * 1.05f;

            // Movement & Mobility cluster (Jump & Crouch)
            m_BtnJump.Pos = new Vector2(rightEdge, bottomEdge - r * 2.2f);
            m_BtnJump.Radius = r * 1.15f;

            m_BtnCrouch.Pos = new Vector2(rightEdge - r * 1.1f, bottomEdge - r * 4.2f);
            m_BtnCrouch.Radius = r * 0.95f;

            // Utility cluster (Use, Drop, Watch, Bag, Craft)
            m_BtnInteract.Pos = new Vector2(rightEdge - r * 3.2f, bottomEdge - r * 4.0f);
            m_BtnInteract.Radius = r * 1.1f;

            m_BtnDrop.Pos = new Vector2(rightEdge - r * 5.2f, bottomEdge - r * 3.4f);
            m_BtnDrop.Radius = r * 0.9f;

            m_BtnInventory.Pos = new Vector2(safeLeft + r * 1.2f, h * 0.35f);
            m_BtnInventory.Radius = r * 0.95f;

            m_BtnCraft.Pos = new Vector2(safeLeft + r * 3.2f, h * 0.25f);
            m_BtnCraft.Radius = r * 0.95f;

            m_BtnWatch.Pos = new Vector2(safeLeft + r * 1.2f, h * 0.15f);
            m_BtnWatch.Radius = r * 0.95f;

            // Top pause button
            m_BtnPause.Pos = new Vector2(safeLeft + r * 1.2f, h * 0.05f + r);
            m_BtnPause.Radius = r * 0.85f;
        }

        void Update()
        {
            VInput.BeginFrame();
            LayoutButtons();

            bool inMenu = (IosCursor.LockState == CursorLockMode.None) || IosCursor.Visible;

            if (inMenu)
            {
                ProcessMenuTouches();
            }
            else
            {
                ProcessGameplayTouches();
            }

            VirtualInput.CommitFrame();
        }

        void ProcessGameplayTouches()
        {
            float w = Screen.width;
            float h = Screen.height;
            float maxStickRadius = h * 0.16f;

            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                Vector2 p = new Vector2(t.position.x, h - t.position.y);

                // 1. Check Button Touches
                bool handledByButton = false;
                foreach (var btn in m_Buttons)
                {
                    if (btn.FingerId == t.fingerId)
                    {
                        handledByButton = true;
                        if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                        {
                            btn.Held = false;
                            btn.FingerId = -1;
                            ReleaseButton(btn);
                        }
                        break;
                    }

                    if (t.phase == TouchPhase.Began && Vector2.Distance(p, btn.Pos) <= btn.Radius * 1.3f)
                    {
                        btn.Held = true;
                        btn.FingerId = t.fingerId;
                        PressButton(btn);
                        handledByButton = true;
                        break;
                    }
                }
                if (handledByButton) continue;

                // 2. Check Joystick on Left Half of screen
                if (t.position.x < w * 0.5f)
                {
                    if (t.phase == TouchPhase.Began && m_StickFinger < 0)
                    {
                        m_StickFinger = t.fingerId;
                        m_StickCenter = p;
                        m_StickPos = p;
                        m_StickEdgeTimer = 0f;
                    }
                    else if (t.fingerId == m_StickFinger)
                    {
                        if (t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary)
                        {
                            m_StickPos = p;
                            Vector2 delta = m_StickPos - m_StickCenter;
                            float dist = delta.magnitude;
                            if (dist > maxStickRadius)
                            {
                                delta = delta.normalized * maxStickRadius;
                                m_StickPos = m_StickCenter + delta;
                            }

                            Vector2 norm = delta / maxStickRadius;
                            VirtualInput.MoveStick = new Vector2(norm.x, -norm.y);

                            if (norm.magnitude >= 0.85f)
                            {
                                m_StickEdgeTimer += Time.unscaledDeltaTime;
                                if (AutoSprint && m_StickEdgeTimer >= 0.15f)
                                {
                                    VInput.SetKey(KeyCode.LeftShift, true);
                                }
                            }
                            else
                            {
                                m_StickEdgeTimer = 0f;
                                VInput.SetKey(KeyCode.LeftShift, false);
                            }
                        }
                        else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                        {
                            m_StickFinger = -1;
                            VirtualInput.MoveStick = Vector2.zero;
                            m_StickEdgeTimer = 0f;
                            VInput.SetKey(KeyCode.LeftShift, false);
                        }
                    }
                }
                // 3. Check Camera Look on Right Half of screen
                else
                {
                    if (t.phase == TouchPhase.Began && m_LookFinger < 0)
                    {
                        m_LookFinger = t.fingerId;
                        m_LookLastPos = t.position;
                    }
                    else if (t.fingerId == m_LookFinger)
                    {
                        if (t.phase == TouchPhase.Moved)
                        {
                            Vector2 delta = t.position - m_LookLastPos;
                            VirtualInput.LookDelta += delta * LookSensitivity;
                            m_LookLastPos = t.position;
                        }
                        else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                        {
                            m_LookFinger = -1;
                        }
                    }
                }
            }
        }

        void ProcessMenuTouches()
        {
            float h = Screen.height;

            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                Vector2 p = t.position;

                if (t.phase == TouchPhase.Began && m_MenuFinger < 0)
                {
                    m_MenuFinger = t.fingerId;
                    VirtualInput.MousePosition = p;
                    VInput.SetMouseButton(0, true);
                }
                else if (t.fingerId == m_MenuFinger)
                {
                    VirtualInput.MousePosition = p;
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                    {
                        m_MenuFinger = -1;
                        VInput.SetMouseButton(0, false);
                    }
                }
            }
        }

        void PressButton(Button b)
        {
            if (b.MouseBtn >= 0) VInput.SetMouseButton(b.MouseBtn, true);
            else if (b.Key != KeyCode.None) VInput.SetKey(b.Key, true);
        }

        void ReleaseButton(Button b)
        {
            if (b.MouseBtn >= 0) VInput.SetMouseButton(b.MouseBtn, false);
            else if (b.Key != KeyCode.None) VInput.SetKey(b.Key, false);
        }

        void OnGUI()
        {
            if (m_HideHud) return;

            bool inMenu = (IosCursor.LockState == CursorLockMode.None) || IosCursor.Visible;
            if (inMenu) return; // Do not draw action HUD during menus/inventory

            if (m_LabelStyle == null)
            {
                m_LabelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold
                };
            }

            Color prev = GUI.color;
            float alpha = HudOpacity;

            // Draw Action Buttons
            foreach (var b in m_Buttons)
            {
                m_LabelStyle.fontSize = Mathf.RoundToInt(b.Radius * 0.48f);
                Rect rect = new Rect(b.Pos.x - b.Radius, b.Pos.y - b.Radius, b.Radius * 2, b.Radius * 2);

                GUI.color = b.Held ? new Color(1f, 0.65f, 0.15f, 0.65f * alpha) : new Color(0.1f, 0.15f, 0.2f, 0.38f * alpha);
                GUI.DrawTexture(rect, m_CircleTex);

                GUI.color = new Color(1f, 1f, 1f, 0.5f * alpha);
                GUI.DrawTexture(rect, m_RingTex);

                GUI.color = new Color(1f, 1f, 1f, 0.95f * alpha);
                GUI.Label(rect, b.Label, m_LabelStyle);
            }

            // Draw Virtual Joystick
            if (m_StickFinger >= 0)
            {
                float outerR = Screen.height * 0.14f;
                float innerR = outerR * 0.42f;

                Rect outerRect = new Rect(m_StickCenter.x - outerR, m_StickCenter.y - outerR, outerR * 2, outerR * 2);
                GUI.color = new Color(1f, 1f, 1f, 0.3f * alpha);
                GUI.DrawTexture(outerRect, m_RingTex);

                Rect innerRect = new Rect(m_StickPos.x - innerR, m_StickPos.y - innerR, innerR * 2, innerR * 2);
                GUI.color = (m_StickEdgeTimer >= 0.15f && AutoSprint) ?
                            new Color(1f, 0.5f, 0.1f, 0.7f * alpha) :
                            new Color(1f, 1f, 1f, 0.55f * alpha);
                GUI.DrawTexture(innerRect, m_CircleTex);
            }

            GUI.color = prev;
        }

        static Texture2D MakeCircleTexture(int size, bool ringOnly)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center)) / center;
                    float a = ringOnly ? Mathf.Clamp01(1f - Mathf.Abs(dist - 0.92f) * 25f)
                                       : Mathf.Clamp01((1f - dist) * center * 0.6f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }
}
