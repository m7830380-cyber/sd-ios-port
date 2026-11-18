"""Pack the unique Metal sources of the converted shaders for the compile check on the macOS runner.

  python tools/extract_msl.py [--work ../sd-work] [--upload]

Reads <work>/data/cache/shaders (written by convert_data.py) and writes <work>/msl.tar.gz. The sources are
derived from the game's shaders, so --upload puts them on the `msl-check` release of the PRIVATE repo only;
.github/workflows/check-msl.yml compiles them there with Apple's compiler.
"""
import argparse, hashlib, io, os, pickle, subprocess, sys, tarfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import convert_shader as C

PRIVATE_REPO = 'm7830380-cyber/sd-ios-private'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', default=os.path.join(os.path.dirname(ROOT), 'sd-work'))
    ap.add_argument('--upload', action='store_true')
    a = ap.parse_args()
    cache = os.path.join(a.work, 'data', 'cache', 'shaders')
    out = os.path.join(a.work, 'msl.tar.gz')
    seen = set()
    with tarfile.open(out, 'w:gz') as tar:
        for f in sorted(os.listdir(cache)):
            nt, _ = pickle.load(open(os.path.join(cache, f), 'rb'))
            if 'm_ParsedForm' not in nt or C.PLATFORM_METAL not in nt['platforms']:
                continue
            name = ''.join(ch if ch.isalnum() else '_' for ch in nt['m_ParsedForm']['m_Name'])
            chunks = C.platform_chunks(nt, nt['compressedBlob'], nt['platforms'].index(C.PLATFORM_METAL))
            for k, e in enumerate(C.entries_of(chunks)):
                s = C.read_sub(C.get_seg(chunks, e))
                hdr = s['code'][12:16]
                src = s['code'][int.from_bytes(hdr, 'little'):]
                h = hashlib.sha1(src).hexdigest()
                if h in seen:
                    continue
                seen.add(h)
                ti = tarfile.TarInfo('msl/%s/%d_%s.metal' % (name, k, 'vs' if s['type'] == C.METAL_VS else 'fs'))
                ti.size = len(src)
                tar.addfile(ti, io.BytesIO(src))
    print('unique sources', len(seen), '->', out)
    if a.upload:
        tag = 'msl-check'
        if subprocess.call(['gh', 'release', 'view', tag, '--repo', PRIVATE_REPO],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) != 0:
            subprocess.check_call(['gh', 'release', 'create', tag, '--repo', PRIVATE_REPO, '--prerelease',
                                   '--title', 'Metal sources for the compile check', '--notes', 'tools/extract_msl.py'])
        subprocess.check_call(['gh', 'release', 'upload', tag, out, '--clobber', '--repo', PRIVATE_REPO])
        print('uploaded to', PRIVATE_REPO, tag)


if __name__ == '__main__':
    main()
