using System;
using System.Collections.Generic;
using SdShim;
using UnityEngine;

namespace SdIOS
{
    /// <summary>
    /// Touch controls. Gameplay: floating joystick (left), camera swipe (right, and while holding ATK/AIM),
    /// action buttons. Menus: tap = click at the finger, plus ESC/BAG/CRAFT so whatever was opened can be closed.
    /// The gear button opens a settings panel (saved in PlayerPrefs).
    /// </summary>
    public sealed class TouchHud : MonoBehaviour
    {
        // ---- Settings (persisted) ---------------------------------------------------------------
        static float LookSensitivity = 1.0f;
        static float HudOpacity = 0.85f;
        static float HudScale = 1.0f;
        static bool AutoSprint = true;
        static bool InvertY;
        static bool LeftHanded;

        static void LoadSettings()
        {
            LookSensitivity = PlayerPrefs.GetFloat("sdios.look", 1.0f);
            HudOpacity = PlayerPrefs.GetFloat("sdios.opacity", 0.85f);
            HudScale = PlayerPrefs.GetFloat("sdios.scale", 1.0f);
            AutoSprint = PlayerPrefs.GetInt("sdios.autosprint", 1) != 0;
            InvertY = PlayerPrefs.GetInt("sdios.inverty", 0) != 0;
            LeftHanded = PlayerPrefs.GetInt("sdios.lefthanded", 0) != 0;
        }

        static void SaveSettings()
        {
            PlayerPrefs.SetFloat("sdios.look", LookSensitivity);
            PlayerPrefs.SetFloat("sdios.opacity", HudOpacity);
            PlayerPrefs.SetFloat("sdios.scale", HudScale);
            PlayerPrefs.SetInt("sdios.autosprint", AutoSprint ? 1 : 0);
            PlayerPrefs.SetInt("sdios.inverty", InvertY ? 1 : 0);
            PlayerPrefs.SetInt("sdios.lefthanded", LeftHanded ? 1 : 0);
            PlayerPrefs.Save();
        }

        // ---- Buttons ----------------------------------------------------------------------------
        sealed class Button
        {
            public string Label;
            public KeyCode Key;
            public int MouseBtn = -1;
            public bool DragLooks;      // dragging off this button turns the camera (ATK, AIM)
            public bool InMenu;         // also shown while a menu / the inventory is open
            public Vector2 Pos;
            public float Radius;
            public bool Held;
            public int FingerId = -1;
            public Vector2 LastPos;
        }

        readonly List<Button> m_Buttons = new List<Button>();
        Button m_BtnAttack, m_BtnAim, m_BtnInteract, m_BtnWatch, m_BtnInventory, m_BtnCraft,
               m_BtnJump, m_BtnCrouch, m_BtnDrop, m_BtnPause, m_BtnEnter;

        // ---- Joystick -----------------------------------------------------------------------------
        const float StickDeadZone = 0.12f;
        const float SprintEdge = 0.9f;
        const float SprintDelay = 0.25f;
        int m_StickFinger = -1;
        Vector2 m_StickCenter, m_StickPos;
        float m_StickEdgeTimer;
        bool m_Sprinting;

        // ---- Look ---------------------------------------------------------------------------------
        int m_LookFinger = -1;
        Vector2 m_LookLastPos;

        // ---- Menu -----------------------------------------------------------------------------------
        int m_MenuFinger = -1;
        bool m_WasInMenu;

        // ---- Settings panel -------------------------------------------------------------------------
        bool m_SettingsOpen;
        Rect m_GearRect;
        Rect m_PanelRect;
        readonly List<KeyValuePair<Rect, Action>> m_PanelHits = new List<KeyValuePair<Rect, Action>>();
        readonly List<KeyValuePair<Rect, string>> m_PanelLabels = new List<KeyValuePair<Rect, string>>();
        int m_PanelFinger = -1;

        Texture2D m_CircleTex, m_RingTex, m_White;
        GUIStyle m_LabelStyle, m_PanelStyle;

        void Awake()
        {
            LoadSettings();
            m_CircleTex = MakeCircleTexture(128, false);
            m_RingTex = MakeCircleTexture(128, true);
            m_White = new Texture2D(1, 1);
            m_White.SetPixel(0, 0, Color.white);
            m_White.Apply();

            m_BtnAttack = Add(new Button { Label = "ATK", MouseBtn = 0, DragLooks = true });
            m_BtnAim = Add(new Button { Label = "AIM", MouseBtn = 1, DragLooks = true });
            m_BtnInteract = Add(new Button { Label = "USE", Key = KeyCode.E });
            m_BtnJump = Add(new Button { Label = "JUMP", Key = KeyCode.Space });
            m_BtnCrouch = Add(new Button { Label = "CROUCH", Key = KeyCode.LeftControl });
            m_BtnDrop = Add(new Button { Label = "DROP", Key = KeyCode.Q });
            m_BtnWatch = Add(new Button { Label = "WATCH", Key = KeyCode.F });
            m_BtnInventory = Add(new Button { Label = "BAG", Key = KeyCode.Tab, InMenu = true });
            m_BtnCraft = Add(new Button { Label = "CRAFT", Key = KeyCode.C, InMenu = true });
            m_BtnPause = Add(new Button { Label = "ESC", Key = KeyCode.Escape, InMenu = true });
            m_BtnEnter = Add(new Button { Label = "ENTER", Key = KeyCode.Return });
        }

        Button Add(Button b) { m_Buttons.Add(b); return b; }

        // GUI coordinates (origin top-left). Mirrored horizontally in left-handed mode.
        float X(float x) => LeftHanded ? Screen.width - x : x;

        bool InGameScene => UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex >= 2;

        bool Visible(Button b, bool inMenu)
        {
            if (!inMenu) return true;
            if (!b.InMenu) return false;
            return b == m_BtnPause || InGameScene;  // BAG / CRAFT mean nothing in the main menu
        }

        void Layout()
        {
            float w = Screen.width, h = Screen.height;
            float r = h * 0.055f * HudScale;
            Rect sa = Screen.safeArea;
            float safeRight = sa.xMax > 0 ? w - sa.xMax : 0f;
            float safeLeft = sa.xMin;
            float safeBottom = sa.yMin;
            float safeTop = sa.yMax > 0 ? h - sa.yMax : 0f;
            float edgeL = Mathf.Max(safeLeft, safeRight);   // landscape notch can be on either side
            float right = w - edgeL - r * 1.3f;
            float bottom = h - safeBottom - r * 1.3f;

            // Thumb arc around the bottom-right corner: ATK biggest, nearest the thumb.
            Place(m_BtnAttack, right - r * 1.9f, bottom - r * 1.2f, r * 1.4f);
            Place(m_BtnJump, right + r * 0.2f, bottom - r * 3.6f, r * 1.05f);
            Place(m_BtnAim, right - r * 4.7f, bottom - r * 0.6f, r * 1.05f);
            Place(m_BtnCrouch, right - r * 4.3f, bottom - r * 3.1f, r * 0.9f);
            Place(m_BtnInteract, right - r * 1.9f, bottom - r * 4.6f, r * 1.1f);
            Place(m_BtnDrop, right - r * 4.6f, bottom - r * 5.4f, r * 0.85f);

            // Utility column top-left (away from the joystick), menu toggles there too.
            float lx = edgeL + r * 1.3f, top = safeTop + r * 1.2f;
            // (not mirrored: the top-right corner belongs to the debug overlay button)
            Place(m_BtnPause, lx, top, r * 0.8f, false);
            Place(m_BtnInventory, lx + r * 2.0f, top, r * 0.8f, false);
            Place(m_BtnCraft, lx + r * 4.0f, top, r * 0.8f, false);
            Place(m_BtnWatch, lx + r * 6.0f, top, r * 0.8f, false);
            Place(m_BtnEnter, lx + r * 8.0f, top, r * 0.8f, false);

            float g = r * 0.75f;
            float gx = lx;
            m_GearRect = new Rect(gx - g * 0.5f, top + r * 1.6f, g, g);
        }

        void Place(Button b, float x, float y, float radius, bool mirror = true)
        {
            b.Pos = new Vector2(mirror ? X(x) : x, y);
            b.Radius = radius;
        }

        // ---- Frame ------------------------------------------------------------------------------
        void Update()
        {
            VInput.BeginFrame();
            Layout();

            // Boot scene ('Press RETURN' screen): any tap continues.
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex == 0)
            {
                for (int i = 0; i < Input.touchCount; i++)
                    if (Input.GetTouch(i).phase == TouchPhase.Began)
                    {
                        VInput.TapKey(KeyCode.Return, 3);
                        VInput.TapKey(KeyCode.Space, 3);
                        VInput.TapKey(KeyCode.KeypadEnter, 3);
                    }
            }

            bool inMenu = IosCursor.LockState == CursorLockMode.None || IosCursor.Visible;
            if (inMenu != m_WasInMenu)
            {
                ReleaseAll();   // a finger held across a menu switch must not keep walking / holding a button
                m_WasInMenu = inMenu;
            }

            if (m_SettingsOpen || SettingsTouch())
                ProcessSettingsTouches();
            else if (inMenu)
                ProcessMenuTouches();
            else
                ProcessGameplayTouches();

            VirtualInput.CommitFrame();
        }

        bool SettingsTouch()
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && m_GearRect.Contains(Gui(t.position)))
                {
                    ReleaseAll();
                    m_SettingsOpen = true;
                    m_PanelFinger = t.fingerId;     // swallow the opening touch
                    return true;
                }
            }
            return false;
        }

        static Vector2 Gui(Vector2 screen) => new Vector2(screen.x, Screen.height - screen.y);

        // ---- Buttons (shared by gameplay and menus) ------------------------------------------------
        /// <returns>true when the touch belongs to a button.</returns>
        bool ButtonTouch(Touch t, Vector2 p, bool inMenu)
        {
            foreach (var btn in m_Buttons)
            {
                if (btn.FingerId != t.fingerId) continue;
                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    btn.Held = false;
                    btn.FingerId = -1;
                    ReleaseButton(btn);
                }
                else if (btn.DragLooks && t.phase == TouchPhase.Moved)
                {
                    AddLook(t.position - btn.LastPos);
                    btn.LastPos = t.position;
                }
                return true;
            }
            if (t.phase != TouchPhase.Began) return false;

            // Nearest visible button within its (generous) hit radius.
            Button best = null;
            float bestD = float.MaxValue;
            foreach (var btn in m_Buttons)
            {
                if (btn.FingerId >= 0 || !Visible(btn, inMenu)) continue;
                float d = Vector2.Distance(p, btn.Pos) / btn.Radius;
                if (d <= 1.25f && d < bestD) { best = btn; bestD = d; }
            }
            if (best == null) return false;
            best.Held = true;
            best.FingerId = t.fingerId;
            best.LastPos = t.position;
            PressButton(best);
            return true;
        }

        void AddLook(Vector2 screenDelta)
        {
            // Pixels -> virtual mouse counts, normalized to a 460 dpi phone so the feel matches across devices.
            float dpiScale = Screen.dpi > 1f ? 460f / Screen.dpi : 1f;
            Vector2 d = screenDelta * LookSensitivity * dpiScale;
            if (InvertY) d.y = -d.y;
            VirtualInput.LookDelta += d;
        }

        // ---- Gameplay ----------------------------------------------------------------------------
        void ProcessGameplayTouches()
        {
            float w = Screen.width, h = Screen.height;
            float maxStickRadius = h * 0.15f * HudScale;

            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                Vector2 p = Gui(t.position);

                if (ButtonTouch(t, p, false)) continue;

                // A finger keeps its role (stick / look) for the whole touch, wherever it moves.
                bool onStickSide = LeftHanded ? t.position.x > w * 0.5f : t.position.x < w * 0.5f;
                bool stick = t.fingerId == m_StickFinger || (t.fingerId != m_LookFinger && onStickSide);
                if (stick)
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
                        if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                        {
                            StopStick();
                            continue;
                        }
                        Vector2 delta = p - m_StickCenter;
                        float dist = delta.magnitude;
                        if (dist > maxStickRadius)
                        {
                            // Drag the base along so reversing direction responds immediately.
                            m_StickCenter = p - delta.normalized * maxStickRadius;
                            delta = p - m_StickCenter;
                            dist = maxStickRadius;
                        }
                        m_StickPos = p;
                        float mag = dist / maxStickRadius;
                        Vector2 dir = dist > 0.001f ? delta / dist : Vector2.zero;
                        // Dead zone, then rescale so movement starts smoothly from 0.
                        float m = mag < StickDeadZone ? 0f : (mag - StickDeadZone) / (1f - StickDeadZone);
                        VirtualInput.MoveStick = new Vector2(dir.x, -dir.y) * m;

                        // Sprint: hold the stick at the edge, mostly forward. Stays on until the stick is released
                        // or pulled back towards the centre.
                        bool forward = -dir.y > 0.5f;
                        if (mag >= SprintEdge && forward)
                            m_StickEdgeTimer += Time.unscaledDeltaTime;
                        else if (mag < 0.6f || !forward)
                            m_StickEdgeTimer = 0f;
                        m_Sprinting = AutoSprint && m_StickEdgeTimer >= SprintDelay;
                        VInput.SetKey(KeyCode.LeftShift, m_Sprinting);
                    }
                }
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
                            AddLook(t.position - m_LookLastPos);
                            m_LookLastPos = t.position;
                        }
                        else if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                            m_LookFinger = -1;
                    }
                }
            }
        }

        void StopStick()
        {
            m_StickFinger = -1;
            m_StickEdgeTimer = 0f;
            m_Sprinting = false;
            VirtualInput.MoveStick = Vector2.zero;
            VInput.SetKey(KeyCode.LeftShift, false);
        }

        // ---- Menus -------------------------------------------------------------------------------
        void ProcessMenuTouches()
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.fingerId != m_MenuFinger && ButtonTouch(t, Gui(t.position), true)) continue;

                // A tap is only a click at the finger (the pointer moves there first, the press follows next frame).
                if (t.phase == TouchPhase.Began)
                {
                    if (m_MenuFinger < 0)
                    {
                        m_MenuFinger = t.fingerId;
                        VirtualInput.MousePosition = t.position;
                        VInput.SetMouseButton(0, true);
                    }
                }
                else if (t.fingerId == m_MenuFinger)
                {
                    VirtualInput.MousePosition = t.position;
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                    {
                        m_MenuFinger = -1;
                        VInput.SetMouseButton(0, false);
                    }
                }
            }
        }

        // ---- Settings panel ---------------------------------------------------------------------
        void ProcessSettingsTouches()
        {
            LayoutPanel();
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.fingerId == m_PanelFinger)
                {
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) m_PanelFinger = -1;
                    continue;
                }
                if (t.phase != TouchPhase.Began) continue;
                m_PanelFinger = t.fingerId;
                Vector2 p = Gui(t.position);
                if (!m_PanelRect.Contains(p)) { CloseSettings(); return; }
                foreach (var hit in m_PanelHits)
                    if (hit.Key.Contains(p)) { hit.Value(); SaveSettings(); break; }
            }
        }

        void CloseSettings()
        {
            m_SettingsOpen = false;
            SaveSettings();
        }

        void LayoutPanel()
        {
            m_PanelHits.Clear();
            m_PanelLabels.Clear();
            float w = Screen.width, h = Screen.height;
            float rowH = h * 0.085f;
            float pw = Mathf.Min(w * 0.6f, h * 1.3f);
            float ph = rowH * 7.6f;
            m_PanelRect = new Rect((w - pw) * 0.5f, (h - ph) * 0.5f, pw, ph);
            float x0 = m_PanelRect.x + rowH * 0.4f, y = m_PanelRect.y + rowH * 0.3f;
            float bw = rowH * 1.3f, right = m_PanelRect.xMax - rowH * 0.4f;

            Row(ref y, rowH, x0, "Look sensitivity  " + LookSensitivity.ToString("0.00"), right, bw,
                () => LookSensitivity = Mathf.Max(0.1f, LookSensitivity - 0.1f),
                () => LookSensitivity = Mathf.Min(5f, LookSensitivity + 0.1f));
            Row(ref y, rowH, x0, "Button size  " + Mathf.RoundToInt(HudScale * 100) + "%", right, bw,
                () => HudScale = Mathf.Max(0.6f, HudScale - 0.1f),
                () => HudScale = Mathf.Min(1.6f, HudScale + 0.1f));
            Row(ref y, rowH, x0, "Button opacity  " + Mathf.RoundToInt(HudOpacity * 100) + "%", right, bw,
                () => HudOpacity = Mathf.Max(0.2f, HudOpacity - 0.1f),
                () => HudOpacity = Mathf.Min(1f, HudOpacity + 0.1f));
            Toggle(ref y, rowH, x0, "Invert camera Y", InvertY, right, bw, () => InvertY = !InvertY);
            Toggle(ref y, rowH, x0, "Sprint at stick edge", AutoSprint, right, bw, () => AutoSprint = !AutoSprint);
            Toggle(ref y, rowH, x0, "Left-handed layout", LeftHanded, right, bw, () => LeftHanded = !LeftHanded);
            var close = new Rect(m_PanelRect.center.x - bw, y + rowH * 0.1f, bw * 2, rowH * 0.8f);
            m_PanelHits.Add(new KeyValuePair<Rect, Action>(close, CloseSettings));
            m_PanelLabels.Add(new KeyValuePair<Rect, string>(close, "[DONE]"));
        }

        void Row(ref float y, float rowH, float x0, string label, float right, float bw, Action minus, Action plus)
        {
            m_PanelLabels.Add(new KeyValuePair<Rect, string>(new Rect(x0, y, right - x0 - bw * 2.2f, rowH), label));
            var m = new Rect(right - bw * 2.1f, y + rowH * 0.1f, bw, rowH * 0.8f);
            var p = new Rect(right - bw, y + rowH * 0.1f, bw, rowH * 0.8f);
            m_PanelHits.Add(new KeyValuePair<Rect, Action>(m, minus));
            m_PanelHits.Add(new KeyValuePair<Rect, Action>(p, plus));
            m_PanelLabels.Add(new KeyValuePair<Rect, string>(m, "[-]"));
            m_PanelLabels.Add(new KeyValuePair<Rect, string>(p, "[+]"));
            y += rowH;
        }

        void Toggle(ref float y, float rowH, float x0, string label, bool on, float right, float bw, Action flip)
        {
            m_PanelLabels.Add(new KeyValuePair<Rect, string>(new Rect(x0, y, right - x0 - bw * 2.2f, rowH), label));
            var b = new Rect(right - bw * 2.1f, y + rowH * 0.1f, bw * 2.1f, rowH * 0.8f);
            m_PanelHits.Add(new KeyValuePair<Rect, Action>(b, flip));
            m_PanelLabels.Add(new KeyValuePair<Rect, string>(b, on ? "[ON]" : "[OFF]"));
            y += rowH;
        }

        // ---- Input plumbing ------------------------------------------------------------------------
        void ReleaseAll()
        {
            foreach (var b in m_Buttons)
            {
                if (b.Held) ReleaseButton(b);
                b.Held = false;
                b.FingerId = -1;
            }
            StopStick();
            m_LookFinger = -1;
            if (m_MenuFinger >= 0) VInput.SetMouseButton(0, false);
            m_MenuFinger = -1;
        }

        static void PressButton(Button b)
        {
            if (b.MouseBtn >= 0) VInput.SetMouseButton(b.MouseBtn, true);
            else if (b.Key != KeyCode.None) VInput.SetKey(b.Key, true);
        }

        static void ReleaseButton(Button b)
        {
            if (b.MouseBtn >= 0) VInput.SetMouseButton(b.MouseBtn, false);
            else if (b.Key != KeyCode.None) VInput.SetKey(b.Key, false);
        }

        // ---- Drawing ---------------------------------------------------------------------------------
        void OnGUI()
        {
            if (m_LabelStyle == null)
            {
                m_LabelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                m_PanelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            }
            Color prev = GUI.color;
            float alpha = HudOpacity;
            bool inMenu = IosCursor.LockState == CursorLockMode.None || IosCursor.Visible;

            foreach (var b in m_Buttons)
            {
                if (!Visible(b, inMenu)) continue;
                m_LabelStyle.fontSize = Mathf.RoundToInt(b.Radius * (b.Label.Length > 4 ? 0.36f : 0.48f));
                Rect rect = new Rect(b.Pos.x - b.Radius, b.Pos.y - b.Radius, b.Radius * 2, b.Radius * 2);
                GUI.color = b.Held ? new Color(1f, 0.65f, 0.15f, 0.65f * alpha) : new Color(0.1f, 0.15f, 0.2f, 0.38f * alpha);
                GUI.DrawTexture(rect, m_CircleTex);
                GUI.color = new Color(1f, 1f, 1f, 0.5f * alpha);
                GUI.DrawTexture(rect, m_RingTex);
                GUI.color = new Color(1f, 1f, 1f, 0.95f * alpha);
                GUI.Label(rect, b.Label, m_LabelStyle);
            }

            // Gear (settings)
            GUI.color = new Color(0.1f, 0.15f, 0.2f, 0.45f * alpha);
            GUI.DrawTexture(m_GearRect, m_CircleTex);
            GUI.color = new Color(1f, 1f, 1f, 0.9f * alpha);
            m_LabelStyle.fontSize = Mathf.RoundToInt(m_GearRect.height * 0.32f);
            GUI.Label(m_GearRect, "SET", m_LabelStyle);

            if (!inMenu && m_StickFinger >= 0)
            {
                float outerR = Screen.height * 0.15f * HudScale;
                float innerR = outerR * 0.42f;
                GUI.color = new Color(1f, 1f, 1f, 0.3f * alpha);
                GUI.DrawTexture(new Rect(m_StickCenter.x - outerR, m_StickCenter.y - outerR, outerR * 2, outerR * 2), m_RingTex);
                Vector2 knob = m_StickCenter + Vector2.ClampMagnitude(m_StickPos - m_StickCenter, outerR);
                GUI.color = m_Sprinting ? new Color(1f, 0.5f, 0.1f, 0.75f * alpha) : new Color(1f, 1f, 1f, 0.55f * alpha);
                GUI.DrawTexture(new Rect(knob.x - innerR, knob.y - innerR, innerR * 2, innerR * 2), m_CircleTex);
            }

            if (m_SettingsOpen)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.DrawTexture(m_PanelRect, m_White);
                foreach (var l in m_PanelLabels)
                {
                    bool btn = l.Value.StartsWith("[");
                    if (btn)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.15f);
                        GUI.DrawTexture(l.Key, m_White);
                    }
                    GUI.color = Color.white;
                    var st = btn ? m_LabelStyle : m_PanelStyle;
                    st.fontSize = Mathf.RoundToInt(Screen.height * 0.032f);
                    GUI.Label(l.Key, btn ? l.Value.Trim('[', ']') : l.Value, st);
                }
            }
            GUI.color = prev;
        }

        static Texture2D MakeCircleTexture(int size, bool ringOnly)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dist = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center)) / center;
                    float a = ringOnly ? Mathf.Clamp01(1f - Mathf.Abs(dist - 0.92f) * 25f)
                                       : Mathf.Clamp01((1f - dist) * center * 0.6f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }
}
