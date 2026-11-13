using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class IosShellBuild
{
    const string ScenePath = "Assets/Scenes/Boot.unity";

    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("SHELL_OUT") ?? "Build/iOS";

        if (!File.Exists(ScenePath))
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

        PlayerSettings.companyName = "Beam Team Games";
        PlayerSettings.productName = "Stranded Deep";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.iOS, "com.beamteam.strandeddeep.ios");
        PlayerSettings.bundleVersion = "1.0";
        PlayerSettings.iOS.buildNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ?? "1";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetArchitecture(BuildTargetGroup.iOS, 1); // ARM64
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.iOS, ManagedStrippingLevel.Minimal);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.iOS, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.iOS, Il2CppCodeGeneration.OptimizeSpeed);
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.iOS, ApiCompatibilityLevel.NET_4_6);
        PlayerSettings.allowUnsafeCode = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.gpuSkinning = true;
        PlayerSettings.MTRendering = true;
        PlayerSettings.iOS.targetOSVersionString = "14.0";
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
        PlayerSettings.iOS.requiresFullScreen = true;
        PlayerSettings.iOS.hideHomeButton = true;
        PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.All;
        PlayerSettings.iOS.statusBarStyle = iOSStatusBarStyle.Default;
        PlayerSettings.statusBarHidden = true;
        PlayerSettings.iOS.appInBackgroundBehavior = iOSAppInBackgroundBehavior.Suspend;
        PlayerSettings.accelerometerFrequency = 0;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.enableInternalProfiler = false;
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        PlayerSettings.usePlayerLog = true;
        PlayerSettings.iOS.appleEnableAutomaticSigning = false;

        AssetDatabase.SaveAssets();

        var report = BuildPipeline.BuildPlayer(new[] { ScenePath }, outDir, BuildTarget.iOS, BuildOptions.None);
        Debug.Log($"[IosShellBuild] result={report.summary.result} errors={report.summary.totalErrors} time={report.summary.totalTime}");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            throw new Exception("Build failed: " + report.summary.result);
    }

    [PostProcessBuild(100)]
    public static void OnPostprocess(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS) return;

        string plistPath = Path.Combine(path, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        var root = plist.root;
        root.SetString("CFBundleDisplayName", "Stranded Deep");
        root.SetBoolean("UIFileSharingEnabled", true);
        root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        root.SetBoolean("UISupportsDocumentBrowser", true);
        root.SetBoolean("GCSupportsControllerUserInteraction", true);
        root.SetBoolean("GCSupportsGameMode", true);
        root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true); // Enable 120Hz on ProMotion
        root.SetBoolean("UIRequiresFullScreen", true);
        root.SetBoolean("UIStatusBarHidden", true);
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);

        var gcProfiles = root.CreateArray("GCSupportedGameControllers");
        gcProfiles.AddDict().SetString("ProfileName", "ExtendedGamepad");
        plist.WriteToFile(plistPath);

        // Native console and crash redirection -> Documents/SD-iOS-native.log
        string mainPath = Path.Combine(path, "MainApp", "main.mm");
        if (!File.Exists(mainPath)) mainPath = Path.Combine(path, "Classes", "main.mm");
        string main = File.ReadAllText(mainPath);
        const string hook = "static void SdRedirectNativeLog()";
        if (!main.Contains(hook))
        {
            string fn = hook + @"
{
    NSString* docs = [NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES) firstObject];
    if (docs == nil) return;
    NSString* cur = [docs stringByAppendingPathComponent: @""SD-iOS-native.log""];
    NSString* prev = [docs stringByAppendingPathComponent: @""SD-iOS-native-prev.log""];
    NSFileManager* fm = [NSFileManager defaultManager];
    [fm removeItemAtPath: prev error: nil];
    [fm moveItemAtPath: cur toPath: prev error: nil];
    if (freopen([cur fileSystemRepresentation], ""w"", stderr) != NULL)
    {
        setvbuf(stderr, NULL, _IONBF, 0);
        dup2(fileno(stderr), fileno(stdout));
        setvbuf(stdout, NULL, _IOLBF, 0);
    }
}

";
            int at = main.IndexOf("int main(", StringComparison.Ordinal);
            if (at < 0) throw new Exception("main() not found in " + mainPath);
            main = main.Insert(at, fn);
            int brace = main.IndexOf('{', main.IndexOf("int main(", StringComparison.Ordinal));
            main = main.Insert(brace + 1, "\n    SdRedirectNativeLog();");
            if (!main.Contains("#include <stdio.h>")) main = "#include <stdio.h>\n#include <unistd.h>\n" + main;
            File.WriteAllText(mainPath, main);
            Debug.Log("[IosShellBuild] Native log redirect added to " + mainPath);
        }

        // Unsigned build config for TrollStore
        string projPath = PBXProject.GetPBXProjectPath(path);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);
        foreach (var guid in new[] { proj.GetUnityMainTargetGuid(), proj.GetUnityFrameworkTargetGuid() })
        {
            proj.SetBuildProperty(guid, "CODE_SIGNING_ALLOWED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGNING_REQUIRED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGN_IDENTITY", "");
            proj.SetBuildProperty(guid, "ENABLE_BITCODE", "NO");
            proj.SetBuildProperty(guid, "IPHONEOS_DEPLOYMENT_TARGET", "14.0");
            proj.SetBuildProperty(guid, "GCC_TREAT_WARNINGS_AS_ERRORS", "NO");
        }
        proj.WriteToFile(projPath);
    }
}
