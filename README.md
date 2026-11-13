# Stranded Deep iOS port (TrollStore)

Runs the retail PC build of Stranded Deep (Unity 2021.2.7f1) natively on iOS (tested on iPhone 14 Pro Max via TrollStore with 120Hz ProMotion support).
It does not rebuild the game: the game's own assemblies are patched and AOT-compiled into an IL2CPP player built with the game's exact Unity version, and the game's own data files are converted for Metal/ASTC and dropped in.

**This repository contains only our own code.** Nothing from the game (assemblies, patched or not, assets, converted data, IPAs with compiled game code) is ever committed here or published from here. Those live in the private repo `sd-ios-private` (patched `Plugins/` + shell/full IPA releases).

* `tools/patcher/` – Mono.Cecil patcher (`SdPatcher`):
  * Win32 (`user32`, `kernel32`), Steamworks (`steam_api64.dll`), Xbox (`XGamingRuntime.dll`), and Rewired Windows P/Invokes become clean managed stubs.
  * Defangs `SteamManager.Awake()` to set initialized to false and smoothly route into Stranded Deep's offline single-player pipeline.
  * Stubs `Rewired.Utils.ExternalTools::GetPlatformInitializer()` to null and `WindowsStandalone_ForwardRawInput()` to no-op, forcing Rewired to fall back to `UnityEngine.Input` routed through `SdShim.VInput`.
  * Patches `FilePath.cctor` to point `ASSET_BUNDLES_FOLDER` to `Path.Combine(Application.dataPath, "Bundles/")` and `DEFAULT_MAPS_FOLDER` to `Path.Combine(Application.dataPath, "Default Maps/")`.
  * Reports `SystemInfo.supportsComputeShaders = false` for `Unity.Postprocessing.Runtime`.
* `shim/` – `SdShim.dll`: virtual legacy input + platform stand-ins the patched game calls (120Hz frame pacing, cursor lock/visibility tracking, resolution ignore, microphone stub).
* `mod/` – `SdIOS.dll`: touch controls tailored for Stranded Deep (virtual thumbstick with edge-sprint, camera swipe, action buttons for Attack, Aim, Interact, Watch, Bag, Craft, Jump, Crouch, Drop, Pause), controller support, on-screen debug overlay and log file.
* `shell/` – minimal Unity 2021.2.7f1 project; GitHub Actions builds the unsigned, ldid-fakesigned shell IPA with extended memory limits and 120Hz ProMotion support.
* `tools/build_plugins.py` – builds shim + mod and runs the patcher into `../sd-ios-private/Plugins`.
* `tools/convert_data.py`, `convert_shader.py`, `dx2metal.py`, `transcode_tex.py` – convert the Windows data for iOS (DX11 → Metal shaders via fixed HLSLcc CLI, BC/DXT → ASTC textures, iOS platform tag).
* `tools/package_ipa.py` – merges the CI shell IPA with the converted data into the final `StrandedDeep.ipa`.

Logs: `Documents/SD-iOS.log` (previous run `SD-iOS-prev.log`); native crash log: `Documents/SD-iOS-native.log`.
