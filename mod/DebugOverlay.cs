using System;
using SdShim;
using UnityEngine;

namespace SdIOS
{
    public sealed class DebugOverlay : MonoBehaviour
    {
        float m_LastTime;
        int m_Frames;
        float m_Fps;
        bool m_ShowLog;
        Vector2 m_LogScroll;
        GUIStyle m_Style;
        GUIStyle m_LogStyle;
        Rect m_ButtonRect;

        void Start()
        {
            m_LastTime = Time.realtimeSinceStartup;
        }

        void Update()
        {
            m_Frames++;
            float now = Time.realtimeSinceStartup;
            float dt = now - m_LastTime;
            if (dt >= 0.5f)
            {
                m_Fps = m_Frames / dt;
                m_Frames = 0;
                m_LastTime = now;
            }
        }

        void OnGUI()
        {
            if (m_Style == null)
            {
                m_Style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.RoundToInt(Screen.height * 0.022f),
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleRight
                };
                m_Style.normal.textColor = new Color(0.1f, 1f, 0.4f, 0.9f);

                m_LogStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.RoundToInt(Screen.height * 0.018f),
                    wordWrap = true
                };
                m_LogStyle.normal.textColor = Color.white;
            }

            float w = Screen.width;
            float h = Screen.height;
            float top = Screen.safeArea.yMax > 0 ? (h - Screen.safeArea.yMax) : 0;
            m_ButtonRect = new Rect(w - 280, top + 8, 270, 36);

            long allocMB = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20;
            string txt = $"{m_Fps:0.0} FPS | {allocMB}MB";

            GUI.color = new Color(0, 0, 0, 0.5f);
            GUI.DrawTexture(m_ButtonRect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(m_ButtonRect.x, m_ButtonRect.y, m_ButtonRect.width - 10, m_ButtonRect.height), txt, m_Style);

            if (Event.current.type == EventType.MouseDown && m_ButtonRect.Contains(Event.current.mousePosition))
            {
                m_ShowLog = !m_ShowLog;
                Event.current.Use();
            }

            if (m_ShowLog)
            {
                Rect logRect = new Rect(w * 0.05f, h * 0.15f, w * 0.9f, h * 0.75f);
                GUI.color = new Color(0, 0, 0, 0.85f);
                GUI.DrawTexture(logRect, Texture2D.whiteTexture);
                GUI.color = Color.white;

                GUILayout.BeginArea(new Rect(logRect.x + 10, logRect.y + 10, logRect.width - 20, logRect.height - 20));
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b>Console Log ({Log.Buffer.Count} entries)</b>", m_LogStyle);
                if (GUILayout.Button("Close", GUILayout.Width(80))) m_ShowLog = false;
                GUILayout.EndHorizontal();

                m_LogScroll = GUILayout.BeginScrollView(m_LogScroll);
                lock (Log.Buffer)
                {
                    for (int i = 0; i < Log.Buffer.Count; i++)
                    {
                        GUILayout.Label(Log.Buffer[i], m_LogStyle);
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.EndArea();
            }
        }
    }
}
