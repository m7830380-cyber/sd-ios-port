using System;
using System.IO;
using SdShim;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SdIOS
{
    public static class Bootstrap
    {
        public static string Documents { get; private set; }
        public static bool OnDevice => Application.platform == RuntimePlatform.IPhonePlayer;
        public static bool TouchEnabled { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void EarlyInit()
        {
            Documents = Platform.DocumentsDir;
            Log.Init(Documents);
            Log.Info($"Stranded Deep iOS initializing. docs={Documents}");
            Log.Info($"device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} gpu={SystemInfo.graphicsDeviceName} " +
                     $"api={SystemInfo.graphicsDeviceType} mem={SystemInfo.systemMemorySize}MB gfxmem={SystemInfo.graphicsMemorySize}MB " +
                     $"screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRate}Hz " +
                     $"dpi={Screen.dpi} safeArea={Screen.safeArea} unity={Application.unityVersion} " +
                     $"compute={SystemInfo.supportsComputeShaders}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            try
            {
                TouchEnabled = OnDevice || File.Exists(Path.Combine(Documents, "touchsim"));
                Input.simulateMouseWithTouches = false;
                Input.multiTouchEnabled = true;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                Screen.orientation = ScreenOrientation.LandscapeLeft;
                Screen.autorotateToLandscapeLeft = true;
                Screen.autorotateToLandscapeRight = true;
                Screen.autorotateToPortrait = false;
                Screen.autorotateToPortraitUpsideDown = false;
                Platform.ApplyFrameRate();

                var go = new GameObject("SD-iOS");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<DebugOverlay>();
                if (TouchEnabled)
                {
                    VInput.Virtual = true;
                    VirtualInput.Init();
                    go.AddComponent<TouchHud>();
                }
                go.AddComponent<Lifecycle>();

                Log.Info($"TouchHud active: {TouchEnabled}, Target FPS: {Application.targetFrameRate}");

                SceneManager.sceneLoaded += (scene, mode) =>
                {
                    Log.Info($"Scene loaded: '{scene.name}' (mode: {mode}) rootCount={scene.rootCount} " +
                             $"totalAlloc={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20}MB " +
                             $"monoUsed={UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() >> 20}MB");
                    Platform.ApplyFrameRate();
                };

                Application.lowMemory += () =>
                {
                    Log.Warn($"[iOS] Low memory warning received! Total allocated: {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20}MB");
                };
            }
            catch (Exception ex)
            {
                Log.Error("Bootstrap.Init failed: " + ex);
            }
        }

        sealed class Lifecycle : MonoBehaviour
        {
            void OnApplicationPause(bool paused) => Log.Info(paused ? "App paused (background)" : "App resumed");
            void OnApplicationQuit() => Log.Info("App quitting");
        }
    }
}
