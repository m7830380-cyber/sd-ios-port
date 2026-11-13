"""Convert retail Stranded Deep (Windows) data so the iOS shell player can load it.

  python tools/convert_data.py [--game "C:/Stranded Deep"] [--out ../sd-work/data] [--jobs 14]

Produces <out>/Data/ (goes into the app's Data folder, see package_ipa.py):
  * Target-platform tag changes from StandaloneWindows64 (19) to iOS (9), and Unity version to 2021.2.7f1.
  * Every Shader gets its DX11 programs translated to Metal (convert_shader.py, HLSLcc).
  * Every BCn/DXT texture is re-encoded as ASTC (transcode_tex.py). Streamed textures go into rebuilt
    <file>.resS together with unchanged streamed mesh data.
  * Asset bundles in Stranded_Deep_Data/Data/Bundles/ get the same shader/texture conversion and go into Data/Bundles/.
  * Default maps in Stranded_Deep_Data/Data/Default Maps/ are copied to Data/Default Maps/.
"""
import argparse, hashlib, json, os, pickle, re, shutil, struct, sys, time, traceback
from concurrent.futures import ProcessPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from convert_shader import convert_shader
import transcode_tex as tex

IOS_PLATFORM = 9
TARGET_UNITY = '2021.2.7f1'
PLAYER_FILES = [
    'globalgamemanagers', 'globalgamemanagers.assets',
    'level0', 'level1', 'level2', 'level3', 'level4',
    'sharedassets0.assets', 'sharedassets1.assets', 'sharedassets2.assets',
    'sharedassets3.assets', 'sharedassets4.assets',
    'resources.assets'
]
DEFAULT_GAME = r'C:\Stranded Deep'
CONVERTER_VERSION = '2'
STREAMED = ('Texture2D', 'Cubemap', 'Texture2DArray', 'Texture3D', 'Mesh')
CHUNK = 160


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def convert_shaders(objects, cache_dir, work_dir, report):
    n = 0
    for obj in objects:
        if obj.type.name != 'Shader':
            continue
        tt = obj.read_typetree()
        blob = bytes(tt['compressedBlob'])
        name = tt['m_ParsedForm']['m_Name']
        h = hashlib.sha1((CONVERTER_VERSION + name).encode())
        h.update(blob)
        cp = os.path.join(cache_dir, h.hexdigest() + '.pkl')
        if os.path.exists(cp):
            nt, stats = pickle.load(open(cp, 'rb'))
        else:
            tt['compressedBlob'] = b''
            msgs = []
            wd = os.path.join(work_dir, 'hlslcc', h.hexdigest())
            res = convert_shader(tt, blob, wd, log=msgs.append)
            shutil.rmtree(wd, ignore_errors=True)
            if res is None:
                report.setdefault('shader_no_dx11', []).append(name)
                continue
            nt, stats = res
            stats = dict(stats, msgs=msgs[:20])
            pickle.dump((nt, stats), open(cp, 'wb'))
        if stats.get('fail'):
            report.setdefault('shader_failures', {})[name] = stats.get('msgs', [])
        obj.save_typetree(nt)
        n += 1
    return n


def _tex_job(args):
    key, fmt, w, h, mips, images, data = args
    try:
        return key, tex.transcode(fmt, w, h, mips, images, data), None
    except Exception as e:
        return key, None, repr(e)


def _apply_texture(obj, tt, res, stream=None):
    tfmt, data, mips = res
    images = max(1, tt.get('m_ImageCount', 1))
    tt['m_TextureFormat'] = tfmt
    tt['m_MipCount'] = mips
    tt['m_CompleteImageSize'] = len(data) // images
    if 'm_PlatformBlob' in tt:
        tt['m_PlatformBlob'] = []
    if stream is not None:
        tt['image data'] = b''
        tt['m_StreamData'] = stream
    else:
        tt['image data'] = data
        if 'm_StreamData' in tt:
            tt['m_StreamData'] = {'offset': 0, 'size': 0, 'path': ''}
    obj.save_typetree(tt)


def _mesh_repoint(obj, path, old_off, size, new_off):
    raw = obj.get_raw_data()
    p = path.encode()
    tail_body = struct.pack('<QIi', old_off, size, len(p)) + p
    pad = (-len(tail_body)) % 4
    for tail in (tail_body + b'\0' * pad, tail_body):
        i = raw.rfind(tail)
        if i >= 0 and len(raw) - i - len(tail) <= 4:
            new = struct.pack('<Q', new_off) + tail[8:]
            obj.set_raw_data(raw[:i] + new + raw[i + len(tail):])
            return True
    return False


def stamp(sf):
    sf._m_target_platform = IOS_PLATFORM
    sf.target_platform = IOS_PLATFORM
    sf.unity_version = TARGET_UNITY
    sf.version = TARGET_UNITY


def process_player_file(src, dst, cache_dir, work_dir, pool):
    report = {}
    env = UnityPy.load(src)
    sf = env.file
    resS_src = src + '.resS'
    resS_dst = dst + '.resS'
    has_resS = os.path.exists(resS_src)
    stream_objs = []
    inline_objs = []
    mesh_objs = []
    for obj in sf.objects.values():
        t = obj.type.name
        if t in tex.TEXTURE_CLASSES:
            tt = obj.read_typetree()
            sd = tt.get('m_StreamData', {})
            if sd.get('size', 0) > 0 and has_resS:
                stream_objs.append((obj, tt))
            else:
                inline_objs.append((obj, tt))
        elif t == 'Mesh' and has_resS:
            tt = obj.read_typetree()
            sd = tt.get('m_StreamData', {})
            if sd.get('size', 0) > 0:
                mesh_objs.append((obj, tt))

    n_sh = convert_shaders(sf.objects.values(), cache_dir, work_dir, report)
    if n_sh:
        report['shaders'] = n_sh

    # Streamed textures + meshes
    if has_resS and (stream_objs or mesh_objs):
        mesh_objs.sort(key=lambda item: item[1]['m_StreamData']['offset'])
        base_name = os.path.basename(resS_dst)
        os.makedirs(os.path.dirname(resS_dst), exist_ok=True)
        with open(resS_src, 'rb') as fin, open(resS_dst, 'wb') as fout:
            for obj, tt in mesh_objs:
                sd = tt['m_StreamData']
                fin.seek(sd['offset'])
                data = fin.read(sd['size'])
                new_off = fout.tell()
                fout.write(data)
                pad = (-len(data)) % 16
                if pad:
                    fout.write(b'\0' * pad)
                if not _mesh_repoint(obj, base_name, sd['offset'], sd['size'], new_off):
                    report.setdefault('mesh_repoint_failed', []).append(tt.get('m_Name', ''))
                report['stream_meshes'] = len(mesh_objs)

            for i in range(0, len(stream_objs), CHUNK):
                batch = stream_objs[i:i + CHUNK]
                jobs = []
                job_indices = []
                for idx, (obj, tt) in enumerate(batch):
                    sd = tt['m_StreamData']
                    fin.seek(sd['offset'])
                    raw = fin.read(sd['size'])
                    if tt.get('m_TextureFormat') not in tex.CONVERTIBLE:
                        # Non-BC texture (e.g. RGBA32, RGB24) natively supported - stream as-is
                        new_off = fout.tell()
                        fout.write(raw)
                        pad = (-len(raw)) % 16
                        if pad:
                            fout.write(b'\0' * pad)
                        tt['m_StreamData']['offset'] = new_off
                        obj.save_typetree(tt)
                        continue
                    jobs.append((idx, tt['m_TextureFormat'], tt['m_Width'], tt['m_Height'], tt.get('m_MipCount', 1), tt.get('m_ImageCount', 1), raw))
                    job_indices.append(idx)
                for idx, res, err in pool.map(_tex_job, jobs):
                    obj, tt = batch[idx]
                    if err:
                        report.setdefault('texture_errors', []).append(err)
                        continue
                    new_off = fout.tell()
                    fout.write(res[1])
                    pad = (-len(res[1])) % 16
                    if pad:
                        fout.write(b'\0' * pad)
                    _apply_texture(obj, tt, res, stream={'offset': new_off, 'size': len(res[1]), 'path': base_name})
            report['stream_textures'] = len(stream_objs)

    # Inline textures
    for i in range(0, len(inline_objs), CHUNK):
        batch = inline_objs[i:i + CHUNK]
        jobs = []
        for idx, (obj, tt) in enumerate(batch):
            if tt.get('m_TextureFormat') not in tex.CONVERTIBLE:
                continue
            data = bytes(obj.read().get_image_data())
            jobs.append((idx, tt['m_TextureFormat'], tt['m_Width'], tt['m_Height'], tt.get('m_MipCount', 1), tt.get('m_ImageCount', 1), data))
        for idx, res, err in pool.map(_tex_job, jobs):
            obj, tt = batch[idx]
            if err:
                report.setdefault('texture_errors', []).append(err)
            else:
                _apply_texture(obj, tt, res)
    if inline_objs:
        report['inline_textures'] = len(inline_objs)

    stamp(sf)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst, 'wb') as f:
        f.write(sf.save())

    # Copy companion .resource (e.g. audio)
    src_res = src.rsplit('.', 1)[0] + '.resource'
    if os.path.exists(src_res):
        dst_res = dst.rsplit('.', 1)[0] + '.resource'
        if not os.path.exists(dst_res):
            shutil.copyfile(src_res, dst_res)
    return report


def process_bundle(src, dst, cache_dir, work_dir, pool):
    report = {}
    env = UnityPy.load(src)
    bf = env.file
    for sf in bf.files.values():
        if not hasattr(sf, 'objects'):
            continue
        convert_shaders(sf.objects.values(), cache_dir, work_dir, report)
        inline = [o for o in sf.objects.values() if o.type.name in tex.TEXTURE_CLASSES]
        for obj in inline:
            tt = obj.read_typetree()
            if tt.get('m_TextureFormat') not in tex.CONVERTIBLE:
                continue
            data = bytes(obj.read().get_image_data())
            key, res, err = _tex_job((0, tt['m_TextureFormat'], tt['m_Width'], tt['m_Height'], tt.get('m_MipCount', 1), tt.get('m_ImageCount', 1), data))
            if err:
                report.setdefault('texture_errors', []).append(err)
            else:
                _apply_texture(obj, tt, res)
        stamp(sf)
    if hasattr(bf, 'version_engine'):
        bf.version_engine = TARGET_UNITY
    data = bf.save(packer='lz4')
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst, 'wb') as f:
        f.write(data)
    return report


def convert_asset_bundles(game_dir, out_data, cache_dir, work_dir, pool, full_report):
    bundles_dir = os.path.join(game_dir, 'Stranded_Deep_Data', 'Data', 'Bundles')
    out_bundles = os.path.join(out_data, 'Bundles')
    if not os.path.exists(bundles_dir):
        return

    for root, _, files in os.walk(bundles_dir):
        for f in files:
            src = os.path.join(root, f)
            rel = os.path.relpath(src, bundles_dir)
            dst = os.path.join(out_bundles, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            if f.endswith(('.unity3d', '')):
                try:
                    full_report[rel] = process_bundle(src, dst, cache_dir, work_dir, pool)
                    log('done bundle', rel)
                except Exception:
                    shutil.copyfile(src, dst)
            else:
                shutil.copyfile(src, dst)

    # Copy Default Maps
    maps_dir = os.path.join(game_dir, 'Stranded_Deep_Data', 'Data', 'Default Maps')
    out_maps = os.path.join(out_data, 'Default Maps')
    if os.path.exists(maps_dir):
        if os.path.exists(out_maps):
            shutil.rmtree(out_maps)
        shutil.copytree(maps_dir, out_maps)
        log('copied Default Maps')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_GAME)
    ap.add_argument('--out', default=os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), 'sd-work', 'data'))
    ap.add_argument('--only', default=None)
    ap.add_argument('--jobs', type=int, default=max(1, (os.cpu_count() or 4) - 2))
    a = ap.parse_args()

    data = os.path.join(a.game, 'Stranded_Deep_Data')
    out_data = os.path.join(a.out, 'Data')
    cache_dir = os.path.join(a.out, 'cache', 'shaders')
    work_dir = os.path.join(a.out, 'tmp')
    os.makedirs(cache_dir, exist_ok=True)

    files = [f for f in PLAYER_FILES if not a.only or a.only in f]
    files.sort(key=lambda f: -os.path.getsize(os.path.join(data, f)))
    full_report = {}
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=a.jobs) as pool:
        for f in files:
            src = os.path.join(data, f)
            log('converting', f)
            try:
                full_report[f] = process_player_file(src, os.path.join(out_data, f), cache_dir, work_dir, pool)
            except Exception:
                full_report[f] = {'error': traceback.format_exc()}
            r = full_report[f]
            log('done', f, {k: (v if not isinstance(v, (list, dict)) else len(v)) for k, v in r.items()})

        if not a.only or 'bundle' in a.only:
            convert_asset_bundles(a.game, out_data, cache_dir, work_dir, pool, full_report)

    json.dump(full_report, open(os.path.join(a.out, 'convert_report.json'), 'w'), indent=1, default=str)
    errors = sum(len(r.get('texture_errors', [])) + ('error' in r) + len(r.get('mesh_repoint_failed', [])) for r in full_report.values())
    log('finished in %.0fs, %d problems' % (time.time() - t0, errors))


if __name__ == '__main__':
    main()
