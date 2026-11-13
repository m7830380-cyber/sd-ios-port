using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SdShim
{
    /// <summary>Stand-ins for desktop-only calls the Cecil patcher redirects here.</summary>
    public static class Platform
    {
        // ---- System.Diagnostics.Process: no child processes allowed on iOS ------------------
        public static Process ProcessStart(string file)
        {
            if (file != null && (file.StartsWith("http://") || file.StartsWith("https://")))
            {
                Application.OpenURL(file);
                return null;
            }
            Debug.LogWarning("[SD-iOS] Process.Start blocked: " + file);
            return null;
        }

        // ---- Documents directory (overridden by -sd-docs <dir> during multi-instance tests) --
        static string s_Docs;

        public static string DocumentsDir
        {
            get
            {
                if (s_Docs != null) return s_Docs;
                s_Docs = Application.persistentDataPath;
                try
                {
                    var a = Environment.GetCommandLineArgs();
                    int i = Array.IndexOf(a, "-sd-docs");
                    if (i >= 0 && i + 1 < a.Length)
                    {
                        Directory.CreateDirectory(a[i + 1]);
                        s_Docs = a[i + 1];
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SD-iOS] -sd-docs error: " + e.Message);
                }
                return s_Docs;
            }
        }

        // ---- Local player identity (Stranded Deep falls back to Environment.UserName/MachineName) -----
        static string playerName;

        public static string PlayerName
        {
            get
            {
                if (playerName != null) return playerName;
                playerName = "Survivor";
                try
                {
                    string p = Path.Combine(DocumentsDir, "PlayerName.txt");
                    if (File.Exists(p))
                    {
                        string n = File.ReadAllText(p).Trim();
                        int nl = n.IndexOfAny(new[] { '\r', '\n' });
                        if (nl >= 0) n = n.Substring(0, nl).Trim();
                        if (n.Length > 0) playerName = n.Length > 32 ? n.Substring(0, 32) : n;
                    }
                    else
                    {
                        File.WriteAllText(p, playerName + "\n");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[SD-iOS] player name error: " + e.Message);
                }
                return playerName;
            }
        }

        public static void SetPlayerName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return;
            if (name.Length > 32) name = name.Substring(0, 32);
            playerName = name;
            try
            {
                File.WriteAllText(Path.Combine(DocumentsDir, "PlayerName.txt"), name + "\n");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SD-iOS] player name write error: " + e.Message);
            }
        }

        // ---- Resolution: iOS screen is always native fullscreen ------------------------------
        public static void SetResolution(int width, int height, bool fullscreen)
        {
            // Desktop resolution setters are no-ops on iOS to prevent overriding native view bounds
        }

        // ---- Frame pacing & 120Hz ProMotion support ------------------------------------------
        static int vSyncCount = 1;
        static int requestedFps = -1;

        /// Highest refresh rate the display supports (120 on iPhone 14 Pro Max ProMotion).
        public static int DisplayMaxFps
        {
            get
            {
                // In Unity 2021.2, refreshRate is an integer on Resolution
                int r = Screen.currentResolution.refreshRate;
                return r >= 30 ? r : 60;
            }
        }

        /// User frame cap (0 = uncapped up to display max, 120 for ProMotion, 60 default)
        public static int UserFpsCap = 120;

        public static int EffectiveTargetFps
        {
            get
            {
                int max = DisplayMaxFps;
                if (UserFpsCap > 0) max = Math.Min(max, UserFpsCap);
                if (vSyncCount > 0 || requestedFps <= 0) return max;
                return Math.Max(30, Math.Min(requestedFps, max));
            }
        }

        public static void SetVSyncCount(int v)
        {
            vSyncCount = v;
            ApplyFrameRate();
        }

        public static void SetTargetFrameRate(int fps)
        {
            requestedFps = fps;
            ApplyFrameRate();
        }

        public static void ApplyFrameRate()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = EffectiveTargetFps;
        }
    }

    /// <summary>
    /// Tracks cursor lock state and visibility so the touch layer knows whether to
    /// route touches as mouse-look or UI click/drag.
    /// </summary>
    public static class IosCursor
    {
        public static CursorLockMode LockState = CursorLockMode.None;
        public static bool Visible = true;
        public static int Version;

        public static void SetLockState(CursorLockMode value)
        {
            LockState = value;
            Version++;
            Cursor.lockState = value;
        }

        public static void SetVisible(bool value)
        {
            Visible = value;
            Version++;
            Cursor.visible = value;
        }

        public static CursorLockMode GetLockState() => LockState;
        public static bool GetVisible() => Visible;
    }

    /// <summary>
    /// Microphone replacement: no audio recording permissions or AVAudioSession disruptions.
    /// </summary>
    public static class Mic
    {
        public static string[] devices => new string[0];
        public static AudioClip Start(string deviceName, bool loop, int lengthSec, int frequency) => null;
        public static void End(string deviceName) { }
        public static bool IsRecording(string deviceName) => false;
        public static int GetPosition(string deviceName) => 0;
        public static void GetDeviceCaps(string deviceName, out int minFreq, out int maxFreq) { minFreq = 0; maxFreq = 0; }
    }
}
