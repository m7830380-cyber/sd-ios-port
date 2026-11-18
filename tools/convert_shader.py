"""Convert a Unity 2021.2 Shader typetree with DX11 programs to a Metal-only Shader typetree.

Layout, measured from the shell's own Unity 2021.2.7f1 iOS shaders (Data/Resources/unity_builtin_extra of the
CI shell IPA, see tools/verify_shaders.py):
  * one flat m_SubPrograms list per program stage; each entry points (m_BlobIndex) at a segment of the
    platform blob. Windows stores each DX11 program once (tier 3); iOS players use tiers 0, 1 and 2.
  * a segment (version 202012090): type, stats[4], keywords, code, sourceMap, vertex channels, then the
    program's own parameters: constant buffers (the first one unnamed and empty) and bindings.
  * the typetree m_Parameters of an entry stays empty; m_CommonParameters may hold parameters shared by all
    programs of a stage. Here every converted segment is self-contained, so the common parameters are cleared
    (DX11 offsets in them would not match the Metal cbuffer structs).
  * Metal code = MetalShaderBlob header (0xF00DCAFE, size 48, version 5, flags 5) + entry name + MSL source.
  * $Globals is named VGlobals / FGlobals per stage (as HLSLcc and Unity's own Metal compiler name it), so the
    vertex and fragment globals never share a name with different layouts.
DX11 vertex/pixel programs are translated with HLSLcc (hlslcc_cli). Geometry/hull/domain programs have no
Metal equivalent: passes that need them are left without programs, as Unity's own Metal build does.
"""
import os, re, json, struct, subprocess, copy, hashlib
import lz4.block
from ukblob import R, W, read_cb, write_cb
from dx2metal import prep_dxbc, merge_params, var_size

HERE = os.path.dirname(os.path.abspath(__file__))


def _find_hlslcc():
    if os.environ.get('HLSLCC_CLI'):
        return os.environ['HLSLCC_CLI']
    for p in (os.path.join(HERE, 'hlslcc_cli', 'build', 'Release', 'hlslcc_cli.exe'),
              os.path.join(HERE, 'hlslcc_cli', 'build', 'Release', 'hlslcc_tool.exe'),
              os.path.join(HERE, 'hlslcc_cli.exe')):
        if os.path.exists(p):
            return p
    return 'hlslcc_cli'


HLSLCC = _find_hlslcc()
BLOB_VERSION = 202012090
PLATFORM_D3D11, PLATFORM_METAL = 4, 14
DX_TYPES = set(range(15, 23))
DX_VS = {15, 16}
DX_PS = {17, 18}
METAL_VS, METAL_FS = 23, 24
HLSLCC_FLAGS = 0x20000 | 0x80000   # TRANSLATE_MATRICES | METAL_SHADOW_SAMPLER_LINEAR
METAL_TIERS = (0, 1, 2)
PROGS = ('progVertex', 'progFragment', 'progGeometry', 'progHull', 'progDomain', 'progRayTracing')
SEM_SRC = {'POSITION': 0, 'NORMAL': 1, 'TANGENT': 2, 'COLOR': 3, 'BLENDWEIGHTS': 12, 'BLENDWEIGHT': 12, 'BLENDINDICES': 13}
GLOBALS = {'VGlobals': '$Globals', 'FGlobals': '$Globals'}


class ConvertError(Exception):
    pass


def metal_code(msl):
    name = b'xlatMtlMain\0'
    hdr = struct.pack('<IIIIIIIIIiiI', 0xF00DCAFE, 48, 5, 48 + len(name), 0, 0, 0, 0, 0, 5, -1, 0)
    return hdr + name + msl


# ---------------- platform blob io ----------------
def platform_chunks(tt, blob, pi):
    out = []
    for j in range(len(tt['offsets'][pi])):
        o, cl, dl = tt['offsets'][pi][j], tt['compressedLengths'][pi][j], tt['decompressedLengths'][pi][j]
        out.append(lz4.block.decompress(blob[o:o + cl], uncompressed_size=dl))
    return out


def entries_of(chunks):
    d = chunks[0]
    n = struct.unpack_from('<i', d, 0)[0]
    return [struct.unpack_from('<iii', d, 4 + 12 * k) for k in range(n)]


def get_seg(chunks, ent):
    o, l, s = ent
    return chunks[s][o:o + l]


def pack_platform(segs, chunk_limit=48 << 20):
    """Segments -> raw chunks, entry table (offset, length, chunk) at the start of chunk 0."""
    n = len(segs)
    table = 4 + 12 * n
    chunks = [bytearray(table)]
    ents = []
    for s in segs:
        if len(chunks[-1]) + len(s) > chunk_limit and len(chunks[-1]) > (table if len(chunks) == 1 else 0):
            chunks.append(bytearray())
        ents.append((len(chunks[-1]), len(s), len(chunks) - 1))
        chunks[-1] += s
    struct.pack_into('<i', chunks[0], 0, n)
    for k, e in enumerate(ents):
        struct.pack_into('<iii', chunks[0], 4 + 12 * k, *e)
    return [bytes(c) for c in chunks]


# ---------------- subprogram segment io (2021.2, version 202012090) ----------------
def read_binds(r):
    binds = []
    for _ in range(r.i()):
        name = r.str(); t = r.i()
        if t == 0: b = dict(kind='tex', name=name, index=r.i(), sampler=r.i(), extra=r.u())
        elif t in (1, 2): b = dict(kind={1: 'cb', 2: 'buf'}[t], name=name, index=r.i(), arraySize=r.i())
        elif t == 3: b = dict(kind='uav', name=name, index=r.i(), orig=r.i())
        elif t == 4: b = dict(kind='sampler', name=name, bind=r.i(), sampler=r.u())
        else: raise ValueError('bind type %d' % t)
        binds.append(b)
    return binds


def read_sub(seg):
    r = R(seg)
    S = {'version': r.i(), 'type': r.i(), 'stats': [r.i() for _ in range(4)]}
    S['keywords'] = [r.str() for _ in range(r.i())]
    n = r.i(); S['code'] = r.bytes(n); r.align()
    S['sourceMap'] = r.i()
    S['channels'] = [(r.i(), r.i()) for _ in range(r.i())]
    S['cbs'] = [read_cb(r) for _ in range(r.i())]
    S['binds'] = read_binds(r)
    S['tail'] = seg[r.p:]
    return S


def write_sub(S):
    w = W(); w.i(S['version']); w.i(S['type'])
    for x in S['stats']: w.i(x)
    w.i(len(S['keywords']))
    for k in S['keywords']: w.str(k)
    w.i(len(S['code'])); w.raw(S['code']); w.align()
    w.i(S['sourceMap']); w.i(len(S['channels']))
    for a, b in S['channels']: w.i(a); w.i(b)
    w.i(len(S['cbs']))
    for cb in S['cbs']: write_cb(w, cb)
    w.i(len(S['binds']))
    for b in S['binds']:
        w.str(b['name'])
        k = b['kind']
        if k == 'tex': w.i(0); w.i(b['index']); w.i(b['sampler']); w.u(b['extra'])
        elif k in ('cb', 'buf'): w.i(1 if k == 'cb' else 2); w.i(b['index']); w.i(b['arraySize'])
        elif k == 'uav': w.i(3); w.i(b['index']); w.i(b['orig'])
        elif k == 'sampler': w.i(4); w.i(b['bind']); w.u(b['sampler'])
    w.raw(S.get('tail', b''))
    return w.get()


# ---------------- reflection -> metal params ----------------
def metal_layout(c):
    n = max(1, c['arraySize'])
    if c['isMatrix']:
        return 16 * c['cols'] * n, 16
    cols = c['cols']
    if cols == 1:
        return 4 * n, 4
    if cols == 2:
        return 8 * n, 8
    return 16 * n, 16


def build_metal_params(refl, msl, P):
    """HLSLcc reflection + original DX params -> (cbs, binds) of a Metal segment (first cb is the empty one)."""
    orig_vars = {}
    for cn, cb in P['cbs'].items():
        for v in cb['vars']:
            orig_vars[(cn, v['name'])] = v
    cbs = [dict(name='', size=0, params=[], structs=[])]
    binds = []
    cur = None
    off = 0
    for r in refl['refl']:
        k = r['k']
        if k == 'cb':
            cur = dict(name=r['name'], size=0, params=[], structs=[])
            cbs.append(cur)
            off = 0
        elif k == 'const':
            if cur is None:
                raise ConvertError('const outside cb')
            size, align = metal_layout(r)
            off = (off + align - 1) // align * align
            ocb = GLOBALS.get(cur['name'], cur['name'])
            ov = orig_vars.get((ocb, r['name']))
            if ov is None:
                raise ConvertError('unknown const %s.%s' % (ocb, r['name']))
            if ov.get('struct'):
                st = dict(ov['struct'], index=off)
                cur['structs'].append(st)
                off += st['size'] * max(1, st['arraySize'])
                cur['size'] = off
                continue
            cur['params'].append(dict(name=r['name'], type=ov['type'], rows=ov['rows'], cols=ov['cols'],
                                      isMatrix=ov['isMatrix'], arraySize=ov['arraySize'], index=off))
            off += size
            cur['size'] = off
        elif k == 'cbbind':
            binds.append(dict(kind='cb', name=r['name'], index=r['index'], arraySize=1))
        elif k == 'tex':
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
                continue
            ot = next((t for t in P['tex'] if t['name'] == r['name']), None)
            if ot is None:
                raise ConvertError('unknown texture %s' % r['name'])
            samp = r['sampler'] if r['sampler'] >= 0 else -1
            binds.append(dict(kind='tex', name=r['name'], index=r['index'], sampler=samp,
                              extra=(ot['dim'] << 1) | (ot['ms'] & 1)))
        elif k == 'buf':
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
            else:
                ob = next((b for b in P['buf'] if b['name'] == r['name']), None)
                binds.append(dict(kind='buf', name=r['name'], index=r['index'], arraySize=ob['arraySize'] if ob else 0))
    for m in re.finditer(rb'sampler sampler_inline(\d+) \[\[ sampler \((\d+)\) \]\]', msl):
        s = next((x for x in P['samplers'] if x['bind'] == int(m.group(1))), None)
        if s is not None:
            binds.append(dict(kind='sampler', name='', bind=int(m.group(2)), sampler=s['sampler']))
    return cbs, binds


def vertex_channels(refl):
    ch = []
    smap = 0
    for r in refl['refl']:
        if r['k'] != 'input':
            continue
        m = re.match(r'([A-Z_]+?)(\d*)$', r['name'])
        sem, idx = m.group(1), int(m.group(2) or 0)
        if sem == 'TEXCOORD':
            src = 4 + idx
        elif sem in SEM_SRC:
            src = SEM_SRC[sem]
        else:
            raise ConvertError('unknown vertex semantic %s' % r['name'])
        ch.append((src, 13 + r['index']))
        smap |= 1 << src
    return smap, ch


def fix_msl(msl, is_vs):
    # Every pass of an object must produce bit-identical depth (depth prepass / forward add / shadows),
    # HLSLcc's Metal output does not guarantee that without invariant.
    if is_vs:
        msl = msl.replace(b'[[ position ]]', b'[[ position, invariant ]]')
    return msl


# Unity inline sampler flags (sampler_LinearClamp etc.): filter in bits 0-1, wrap U/V/W in bits 2-7.
INLINE_FILTER = {0: b'filter::nearest, mip_filter::nearest', 1: b'filter::linear, mip_filter::nearest',
                 2: b'filter::linear, mip_filter::linear'}
INLINE_SEEN = {}   # flags -> count, reported by convert_data
INLINE_WRAP = {0: b'repeat', 1: b'clamp_to_edge', 2: b'mirrored_repeat', 3: b'mirrored_repeat'}


def inline_samplers(msl, P):
    """Bake HLSL inline samplers into the MSL as constexpr samplers, like Unity's own Metal compiler.
    Unity's Metal runtime does not set up sampler states for a bound slot with no texture behind it,
    so a bound inline sampler falls back to repeat wrapping (curves sampled at 1.0 wrap to 0)."""
    pat = re.compile(rb'(,?)\s*\n\s*sampler sampler_inline(\d+) \[\[ sampler \((\d+)\) \]\](,?)')
    decls = []

    def sub(m):
        s = next((x for x in P['samplers'] if x['bind'] == int(m.group(2))), None)
        flags = s['sampler'] if s else None
        INLINE_SEEN[flags] = INLINE_SEEN.get(flags, 0) + 1
        if flags is None or flags >> 8 or (flags & 3) not in INLINE_FILTER:
            return m.group(0)   # unknown (e.g. comparison sampler): leave it bound
        w = [INLINE_WRAP[(flags >> sh) & 3] for sh in (2, 4, 6)]
        decls.append(b'constexpr sampler sampler_inline%s(%s, s_address::%s, t_address::%s, r_address::%s);\n'
                     % (m.group(2), INLINE_FILTER[flags & 3], w[0], w[1], w[2]))
        return b',' if m.group(1) and m.group(4) else b''
    out = pat.sub(sub, msl)
    if not decls:
        return msl
    at = re.search(rb'\n(vertex|fragment|kernel) ', out)
    return out[:at.start() + 1] + b''.join(decls) + out[at.start() + 1:]


# ---------------- hlslcc batch ----------------
class HlslccBatch:
    def __init__(self, workdir):
        self.dir = workdir
        os.makedirs(workdir, exist_ok=True)
        self.jobs = []

    def add(self, key, dxbc):
        open(os.path.join(self.dir, key) + '.dxbc', 'wb').write(dxbc)
        self.jobs.append(key)

    def _line(self, key):
        p = os.path.join(self.dir, key)
        return f'{p}.dxbc\t{p}.metal\t{p}.json\t{HLSLCC_FLAGS}\n'

    def run(self, nproc=None):
        if not self.jobs:
            return
        nproc = max(1, min(nproc or (os.cpu_count() or 2) - 1, len(self.jobs)))
        procs = []
        for i in range(nproc):
            lf = os.path.join(self.dir, 'list%d.txt' % i)
            with open(lf, 'w') as f:
                f.writelines(self._line(k) for k in self.jobs[i::nproc])
            procs.append(subprocess.Popen([HLSLCC, lf], stderr=subprocess.DEVNULL, stdout=subprocess.DEVNULL))
        for p in procs:
            p.wait()
        for key in self.jobs:   # a crash kills the rest of its list: retry those one by one
            if not os.path.exists(os.path.join(self.dir, key) + '.json'):
                lf = os.path.join(self.dir, 'single.txt')
                with open(lf, 'w') as f:
                    f.write(self._line(key))
                subprocess.run([HLSLCC, lf], stderr=subprocess.DEVNULL, stdout=subprocess.DEVNULL)

    def result(self, key):
        p = os.path.join(self.dir, key)
        if not os.path.exists(p + '.json'):
            return None, None
        refl = json.loads(open(p + '.json', 'rb').read().decode('utf8', 'replace'))
        if not refl.get('ok'):
            return None, refl
        return open(p + '.metal', 'rb').read(), refl


# ---------------- shared cbuffer layouts ----------------
def dx_params(pr, names, sub):
    return merge_params(pr['m_CommonParameters'], names, dict(cbs=sub['cbs'], binds=sub['binds']))


def _vkey(v):
    return (v['offset'], v['cols'], v['rows'], v['isMatrix'], v['arraySize'], v['type'], bool(v.get('struct')))


def cb_unions(tt, blob):
    """Named cbuffer -> {size, vars: name -> var} over every DX11 vertex/pixel program of a shader, plus the
    names whose programs disagree (same variable at another offset / type)."""
    union, bad = {}, set()
    if PLATFORM_D3D11 not in tt['platforms']:
        return union, bad
    chunks = platform_chunks(tt, bytes(blob), tt['platforms'].index(PLATFORM_D3D11))
    ents = entries_of(chunks)
    for ss in tt['m_ParsedForm']['m_SubShaders']:
        for p in ss['m_Passes']:
            names = {v: k for k, v in p['m_NameIndices']}
            for prog in PROGS:
                pr = p[prog]
                for e in pr['m_SubPrograms']:
                    if e['m_GpuProgramType'] not in DX_VS | DX_PS:
                        continue
                    try:
                        P = dx_params(pr, names, read_sub(get_seg(chunks, ents[e['m_BlobIndex']])))
                    except Exception:
                        continue
                    for cn, cb in P['cbs'].items():
                        if cn == '$Globals':
                            continue
                        merge_union(union, bad, cn, cb['size'], cb['vars'])
    return union, bad


def merge_union(union, bad, cn, size, vars_):
    u = union.setdefault(cn, dict(size=0, vars={}))
    u['size'] = max(u['size'], size)
    for v in (vars_.values() if isinstance(vars_, dict) else vars_):
        o = u['vars'].setdefault(v['name'], v)
        if _vkey(o) != _vkey(v):
            bad.add(cn)


def finish_layouts(union, bad):
    """-> {cbname: dict(size, vars sorted by offset)} for the consistent, non-overlapping cbuffers."""
    out = {}
    for cn, u in union.items():
        if cn in bad:
            continue
        vs = sorted(u['vars'].values(), key=lambda v: v['offset'])
        if any(a['offset'] + var_size(a) > b['offset'] for a, b in zip(vs, vs[1:])):
            continue
        out[cn] = dict(size=u['size'], vars=vs)
    return out


def shared_cb_layouts(tt, blob, name, log, global_layouts=None):
    """Named cbuffer (UnityPerDraw, UnityPerCamera, UnityPerMaterial, ...) -> the variable list every program
    declares. DX11 parameters list only what each program uses and HLSLcc packs the Metal struct from what it
    is told, so programs would disagree on member offsets of the same named buffer (vertex vs fragment stage,
    and one shader vs another). Unity's own Metal compiler declares the complete cbuffer everywhere; the same
    is done here with the union over all game shaders (global_layouts, see convert_data.collect_layouts), or
    over this shader's programs for a cbuffer whose declarations differ between shaders.
    $Globals stays per program (VGlobals / FGlobals)."""
    union, bad = cb_unions(tt, blob)
    local = finish_layouts(union, bad)
    out = {}
    for cn in union:
        if global_layouts and cn in global_layouts and all(
                v['name'] in {g['name'] for g in global_layouts[cn]['vars']} for v in union[cn]['vars'].values()):
            out[cn] = global_layouts[cn]
        elif cn in local:
            out[cn] = local[cn]
        else:
            log('  [%s] cbuffer %s: variants disagree, layout left per program' % (name, cn))
    return out


# ---------------- main conversion ----------------
def convert_shader(tt, blob, workdir, log=print, global_layouts=None):
    """tt: Shader typetree (compressedBlob emptied), blob: compressed blob bytes.
    Returns (new typetree incl. compressedBlob, stats) or None when the shader has no DX11 programs."""
    if PLATFORM_D3D11 not in tt['platforms']:
        return None
    blob = bytes(blob)
    pi = tt['platforms'].index(PLATFORM_D3D11)
    chunks = platform_chunks(tt, blob, pi)
    ents = entries_of(chunks)
    name = tt['m_ParsedForm']['m_Name']
    batch = HlslccBatch(workdir)
    plan = []       # (key, P, sub, is_vs)
    keymap = {}     # (subshader, pass, prog, blob index) -> key or None
    by_digest = {}  # sha1 of the prepared DXBC -> key
    stats = dict(ok=0, fail=0, dropped=0)
    layouts = shared_cb_layouts(tt, blob, name, log, global_layouts)
    for si, ss in enumerate(tt['m_ParsedForm']['m_SubShaders']):
        for pa, p in enumerate(ss['m_Passes']):
            names = {v: k for k, v in p['m_NameIndices']}
            for prog in PROGS:
                pr = p[prog]
                for e in pr['m_SubPrograms']:
                    t = e['m_GpuProgramType']
                    if t not in DX_TYPES:
                        continue
                    if t not in DX_VS | DX_PS:
                        stats['dropped'] += 1
                        continue
                    ck = (si, pa, prog, e['m_BlobIndex'])
                    if ck in keymap:
                        continue
                    key = 'k%d' % len(keymap)
                    keymap[ck] = key
                    try:
                        sub = read_sub(get_seg(chunks, ents[e['m_BlobIndex']]))
                        P = dx_params(pr, names, sub)
                        for cn, cb in P['cbs'].items():
                            if cn in layouts:
                                cb['vars'] = [dict(v) for v in layouts[cn]['vars']]
                                cb['size'] = layouts[cn]['size']
                        dx, _ = prep_dxbc(sub['code'], P)
                        dig = hashlib.sha1(dx).hexdigest()   # passes often share identical programs
                        if dig in by_digest:
                            keymap[ck] = by_digest[dig]
                            continue
                        by_digest[dig] = key
                        batch.add(key, dx)
                        plan.append((key, P, sub, t in DX_VS))
                    except Exception as ex:
                        keymap[ck] = None
                        stats['fail'] += 1
                        log('  [%s] prep failed: %r' % (name, ex))
    batch.run()

    segs = []
    results = {}    # key -> (segment index, channels, sourceMap)
    for key, P, sub, is_vs in plan:
        msl, refl = batch.result(key)
        if msl is None:
            stats['fail'] += 1
            diag = [d['msg'] for d in refl['refl'] if d['k'] == 'diag'] if refl else None
            log('  [%s] hlslcc failed%s %s' % (name, (': ' + str(diag)[:300]) if diag else ' (crash)', sub['keywords']))
            continue
        try:
            msl = inline_samplers(fix_msl(msl, is_vs), P)
            cbs, binds = build_metal_params(refl, msl, P)
            smap, ch = vertex_channels(refl) if is_vs else (0, [])
            msub = dict(version=BLOB_VERSION, type=METAL_VS if is_vs else METAL_FS, stats=[0, 0, 0, 0],
                        keywords=sub['keywords'], code=metal_code(msl), sourceMap=smap, channels=ch,
                        cbs=cbs, binds=binds, tail=b'')
            segs.append(write_sub(msub))
            results[key] = (len(segs) - 1, ch, smap)
            stats['ok'] += 1
        except Exception as ex:
            stats['fail'] += 1
            log('  [%s] build failed: %r %s' % (name, ex, sub['keywords']))

    nt = copy.deepcopy(tt)
    for si, ss in enumerate(nt['m_ParsedForm']['m_SubShaders']):
        for pa, p in enumerate(ss['m_Passes']):
            if p.get('m_Platforms'):
                p['m_Platforms'] = [PLATFORM_METAL] if PLATFORM_D3D11 in p['m_Platforms'] else []
            # A pass that needs geometry/tessellation stages cannot run without them: leave it without programs.
            needs_missing = any(e['m_GpuProgramType'] in DX_TYPES - DX_VS - DX_PS
                                for prog in PROGS for e in p[prog]['m_SubPrograms'])
            for prog in PROGS:
                pr = p[prog]
                out = []
                for e in (pr['m_SubPrograms'] if not needs_missing else []):
                    if e['m_GpuProgramType'] not in DX_VS | DX_PS:
                        continue
                    key = keymap.get((si, pa, prog, e['m_BlobIndex']))
                    r = results.get(key) if key else None
                    if r is None:
                        continue
                    mi, ch, smap = r
                    ne = dict(e)
                    ne['m_BlobIndex'] = mi
                    ne['m_GpuProgramType'] = METAL_VS if e['m_GpuProgramType'] in DX_VS else METAL_FS
                    ne['m_Channels'] = dict(m_Channels=[dict(source=s, target=t) for s, t in ch], m_SourceMap=smap)
                    ne['m_Parameters'] = {k: [] for k in e['m_Parameters']}
                    for tier in METAL_TIERS:
                        te = dict(ne)
                        te['m_ShaderHardwareTier'] = tier
                        out.append(te)
                pr['m_SubPrograms'] = out
                pr['m_CommonParameters'] = {k: [] for k in pr['m_CommonParameters']}

    raw = pack_platform(segs)
    comp = [lz4.block.compress(c, store_size=False, mode='high_compression') for c in raw]
    offs, o = [], 0
    for c in comp:
        offs.append(o)
        o += len(c)
    nt['platforms'] = [PLATFORM_METAL]
    nt['offsets'] = [offs]
    nt['compressedLengths'] = [[len(c) for c in comp]]
    nt['decompressedLengths'] = [[len(c) for c in raw]]
    if 'stageCounts' in nt:
        nt['stageCounts'] = [tt['stageCounts'][pi]] if tt.get('stageCounts') else []
    nt['compressedBlob'] = b''.join(comp)
    log('  [%s] variants ok=%d fail=%d dropped(non vs/ps)=%d' % (name, stats['ok'], stats['fail'], stats['dropped']))
    return nt, stats
