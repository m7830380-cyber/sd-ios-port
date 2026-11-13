"""Build everything that goes into the shell's Assets/Plugins/Game folder.

  python tools/build_plugins.py [--game "C:/Stranded Deep"] [--out ../sd-ios-private/Plugins]

1. builds SdShim.dll (shim/) and the Cecil patcher (tools/patcher)
2. patches the retail Stranded Deep assemblies into --out
3. builds SdIOS.dll (mod/) against them
4. writes Unity .meta files (iOS only, not loaded in the Editor)

--out is the private repo's Plugins folder; CI checks it out into shell/Assets/Plugins/Game.
Nothing produced here may be committed to the public repo - it is derived from the game.
"""
import argparse, hashlib, os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

META = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 1
  isExplicitlyReferenced: 0
  validateReferences: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      iPhone: iOS
    second:
      enabled: 1
      settings:
        AddToEmbeddedBinaries: false
        CPU: AnyCPU
        CompileFlags:
        FrameworkDependencies:
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def run(*cmd):
    print('>', ' '.join(cmd), flush=True)
    subprocess.check_call(cmd)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=r'C:\Stranded Deep')
    ap.add_argument('--out', default=os.path.join(os.path.dirname(ROOT), 'sd-ios-private', 'Plugins'))
    a = ap.parse_args()
    managed = os.path.join(a.game, 'Stranded_Deep_Data', 'Managed')
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)
    for f in os.listdir(out):
        if f.endswith(('.dll', '.meta')):
            os.remove(os.path.join(out, f))

    run('dotnet', 'build', os.path.join(ROOT, 'shim'), '-c', 'Release', '-nologo', '-v', 'q', f'-p:ManagedDir={managed}')
    run('dotnet', 'build', os.path.join(ROOT, 'tools', 'patcher'), '-c', 'Release', '-nologo', '-v', 'q')
    shim = os.path.join(ROOT, 'shim', 'bin', 'Release', 'netstandard2.1', 'SdShim.dll')
    patcher = os.path.join(ROOT, 'tools', 'patcher', 'bin', 'Release', 'net8.0', 'SdPatcher.dll')
    run('dotnet', patcher, 'patch', managed, shim, out)
    run('dotnet', 'build', os.path.join(ROOT, 'mod'), '-c', 'Release', '-nologo', '-v', 'q',
        f'-p:ManagedDir={managed}', f'-p:GameAsmDir={out}')
    shutil.copyfile(os.path.join(ROOT, 'mod', 'bin', 'Release', 'netstandard2.1', 'SdIOS.dll'), os.path.join(out, 'SdIOS.dll'))

    n = 0
    for f in sorted(os.listdir(out)):
        if not f.endswith('.dll'):
            continue
        guid = hashlib.md5(('sd-ios-plugin:' + f).encode()).hexdigest()
        with open(os.path.join(out, f + '.meta'), 'w', newline='\n') as fh:
            fh.write(META.format(guid=guid))
        n += 1
    print(f'{n} plugins prepared in {out}')


if __name__ == '__main__':
    main()
