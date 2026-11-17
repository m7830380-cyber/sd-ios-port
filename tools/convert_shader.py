"""Convert a Unity 2021.2 Shader typetree with DX11 programs to a Metal-only Shader typetree.

Unity 2021.2 layout:
  * Flat m_SubPrograms list per program stage (progVertex, progFragment, ...);
  * Each subprogram entry points (m_BlobIndex) into the decompressed blob of its platform;
  * In Unity 2021.2, each subprogram segment in the platform blob contains its own constant
    buffers and resource bindings directly appended after vertex channels (version 202012090).
    There are no separate parameter segments in the blob.
  * Typetree subprogram entries carry their parameter bindings (m_ConstantBuffers, m_TextureParams, etc.)
    and channels (m_Channels, m_SourceMap) matching the Metal shader layout.
  * Metal code uses the Unity MetalShaderBlob header (0xF00DCAFE, header size 48, version 5, flags 5).
  * Shaders are tagged for platform Metal (14) with hardware tiers [0, 1, 2].
"""
import os, re, json, struct, subprocess, copy, hashlib
import lz4.block
from ukblob import R, W, read_cb, write_cb
from dx2metal import prep_dxbc, merge_params, var_size

HERE = os.path.dirname(os.path.abspath(__file__))

def _find_hlslcc():
    if os.environ.get('HLSLCC_CLI'):
        return os.environ['HLSLCC_CLI']
    for cand in ['hlslcc_cli.exe', 'hlslcc_tool.exe', 'hlslcc_cli', 'hlslcc_tool']:
        for sub in [('build', 'Release'), ('build',), ()]:
            p = os.path.join(HERE, 'hlslcc_cli', *sub, cand) if sub else os.path.join(HERE, cand)
            if os.path.exists(p): return p
    return os.path.join(HERE, 'hlslcc_cli.exe')

HLSLCC = _find_hlslcc()
BLOB_VERSION = 202012090
PLATFORM_D3D11, PLATFORM_METAL = 4, 14
DX_TYPES = set(range(15, 23))
DX_VS = {15, 16}
DX_PS = {17, 18}
METAL_VS, METAL_FS = 23, 24
HLSLCC_FLAGS = 0x20000 | 0x80000   # TRANSLATE_MATRICES | METAL_SHADOW_SAMPLER_LINEAR
METAL_TIERS = [0, 1, 2]
PROGS = ('progVertex', 'progFragment', 'progGeometry', 'progHull', 'progDomain', 'progRayTracing')
SEM_SRC = {'POSITION': 0, 'NORMAL': 1, 'TANGENT': 2, 'COLOR': 3, 'BLENDWEIGHTS': 12, 'BLENDWEIGHT': 12, 'BLENDINDICES': 13}
INSTANCING_KW = {'INSTANCING_ON', 'PROCEDURAL_INSTANCING_ON', 'STEREO_INSTANCING_ON'}

class ConvertError(Exception):
    pass

def metal_code(msl, is_vs=False):
    name = b'xlatMtlMain\0'
    flags = 5
    hdr = struct.pack('<IIIIIIIIIiiI', 0xF00DCAFE, 48, 5, 48 + len(name), 0, 0, 0, 0, 0, flags, -1, 0)
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
    for k, (o, l, s) in enumerate(ents):
        struct.pack_into('<iii', chunks[0], 4 + 12 * k, o, l, s)
    return [bytes(c) for c in chunks]

# ---------------- subprogram segment io ----------------
def read_sub(seg):
    r = R(seg)
    S = {'version': r.i(), 'type': r.i(), 'stats': [r.i() for _ in range(4)]}
    S['keywords'] = [r.str() for _ in range(r.i())]
    if 201806140 <= S['version'] <= 202012089:
        S['localKw'] = [r.str() for _ in range(r.i())]
    else:
        S['localKw'] = []
    n = r.i(); S['code'] = r.bytes(n); r.align()
    S['sourceMap'] = r.i()
    S['channels'] = [(r.i(), r.i()) for _ in range(r.i())]
    S['cbs'] = [read_cb(r) for _ in range(r.i())]
    binds = []
    for _ in range(r.i()):
        name = r.str(); t = r.i()
        if t == 0: b = dict(kind='tex', name=name, index=r.i(), sampler=r.i(), extra=r.u())
        elif t in (1, 2): b = dict(kind={1: 'cb', 2: 'buf'}[t], name=name, index=r.i(), arraySize=r.i())
        elif t == 3: b = dict(kind='uav', name=name, index=r.i(), orig=r.i())
        elif t == 4: b = dict(kind='sampler', name=name, bind=r.i(), sampler=r.u())
        else: raise ValueError(f'bind type {t}')
        binds.append(b)
    S['binds'] = binds
    S['tail'] = seg[r.p:]
    return S

def write_sub(S):
    w = W(); w.i(S['version']); w.i(S['type'])
    for x in S['stats']: w.i(x)
    w.i(len(S['keywords']))
    for k in S['keywords']: w.str(k)
    if 201806140 <= S['version'] <= 202012089:
        w.i(len(S['localKw']))
        for k in S['localKw']: w.str(k)
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

# ---------------- parameters & reflection ----------------
def metal_layout(c):
    n = max(1, c['arraySize'])
    if c['isMatrix']: return 16 * c['cols'] * n, 16
    cols = c['cols']
    if cols == 1: return 4 * n, 4
    if cols == 2: return 8 * n, 8
    return 16 * n, 16

def build_metal_params(refl, msl, P, cbname_map):
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
            cb_name = cbname_map.get(r['name'], r['name'])
            cur = dict(name=cb_name, size=0, params=[], structs=[])
            cbs.append(cur)
            off = 0
        elif k == 'const':
            if cur is None: raise ConvertError('const outside cb')
            size, align = metal_layout(r)
            off = (off + align - 1) // align * align
            ocb = cbname_map.get(cur['name'], cur['name'])
            ov = orig_vars.get((ocb, r['name']))
            if ov is None:
                ov = orig_vars.get((r['name'], r['name']))
            if ov is None:
                ov = next((v for (c, n), v in orig_vars.items() if n == r['name'] or n.lstrip('_') == r['name'].lstrip('_')), None)
            if ov is None:
                ov = dict(name=r['name'], type=r.get('type', 0), rows=r.get('rows', 1), cols=r.get('cols', 4),
                          isMatrix=r.get('isMatrix', 0), arraySize=r.get('arraySize', 0))
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
            cb_name = cbname_map.get(r['name'], r['name'])
            binds.append(dict(kind='cb', name=cb_name, index=r['index'], arraySize=1))
        elif k == 'tex':
            ot = next((t for t in P['tex'] if t['name'] == r['name']), None)
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
                continue
            dim = ot['dim'] if ot else r.get('dim', 2)
            ms = (ot['ms'] & 1) if ot else (r.get('ms', 0) & 1)
            samp = r['sampler'] if r['sampler'] >= 0 else -1
            binds.append(dict(kind='tex', name=r['name'], index=r['index'], sampler=samp,
                              extra=(dim << 1) | ms))
        elif k == 'buf':
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
            else:
                ob = next((b for b in P['buf'] if b['name'] == r['name']), None)
                binds.append(dict(kind='buf', name=r['name'], index=r['index'], arraySize=ob['arraySize'] if ob else 0))
    for m in re.finditer(rb'sampler sampler_inline(\d+)\s*\[\[\s*sampler\s*\(\s*(\d+)\s*\)\s*\]\]', msl):
        s = next((x for x in P['samplers'] if x['bind'] == int(m.group(1))), None)
        if s is not None:
            binds.append(dict(kind='sampler', name='', bind=int(m.group(2)), sampler=s['sampler']))
    # CB 0 is always the dummy unnamed/globals buffer (required by UnityFramework's blob deserializer)
    cbs = [cbs[0]] + [cb for cb in cbs[1:] if cb['name'] != '' and (cb['params'] or cb['size'] > 0)]
    return cbs, binds

def vertex_channels(refl):
    ch = []
    smap = 0
    for r in refl['refl']:
        if r['k'] != 'input': continue
        m = re.match(r'([A-Z_]+?)(\d*)$', r['name'])
        sem, idx = m.group(1), int(m.group(2) or 0)
        if sem == 'TEXCOORD': src = 4 + idx
        elif sem in SEM_SRC: src = SEM_SRC[sem]
        else: raise ConvertError(f'unknown vertex semantic {r["name"]}')
        ch.append((src, 13 + r['index']))
        smap |= 1 << src
    return smap, ch

# Unity inline sampler flags (sampler_LinearClamp etc.): filter in bits 0-1, wrap U/V/W in bits 2-7.
INLINE_FILTER = {0: b'filter::nearest, mip_filter::nearest', 1: b'filter::linear, mip_filter::nearest',
                 2: b'filter::linear, mip_filter::linear'}
INLINE_SEEN = {}   # flags -> count, reported by convert_data
INLINE_WRAP = {0: b'repeat', 1: b'clamp_to_edge', 2: b'mirrored_repeat', 3: b'mirrored_repeat'}

def inline_samplers(msl, P):
    """Bake HLSL inline samplers into the MSL as constexpr samplers, like Unity's own Metal compiler.
    Unity's Metal runtime does not set up sampler states for a bound slot with no texture behind it,
    so a bound inline sampler falls back to repeat wrapping (curves sampled at 1.0 wrap to 0)."""
    pat = re.compile(rb'(,?)\s*\n\s*sampler sampler_inline(\d+)\s*\[\[\s*sampler\s*\(\s*(\d+)\s*\)\s*\]\]\s*(,?)')
    decls = []

    def sub(m):
        s = next((x for x in P['samplers'] if x['bind'] == int(m.group(2))), None)
        flags = s['sampler'] if s else None
        INLINE_SEEN[flags] = INLINE_SEEN.get(flags, 0) + 1
        if flags is None or flags >> 8 or (flags & 3) not in INLINE_FILTER:
            return m.group(0)   # unknown (e.g. comparison sampler): leave it bound
        w = [INLINE_WRAP[(flags >> sh) & 3] for sh in (2, 4, 6)]
        decl = (b'constexpr sampler sampler_inline%s(%s, s_address::%s, t_address::%s, r_address::%s);\n'
                % (m.group(2), INLINE_FILTER[flags & 3], w[0], w[1], w[2]))
        if decl not in decls:
            decls.append(decl)
        return b',' if m.group(1) and m.group(4) else b''

    out = pat.sub(sub, msl)
    if not decls:
        return msl
    at = re.search(rb'\n(vertex|fragment|kernel) ', out)
    if at:
        return out[:at.start() + 1] + b''.join(decls) + out[at.start() + 1:]
    return b''.join(decls) + out

def fix_msl(msl, is_vs):
    if is_vs:
        msl = msl.replace(b'[[ position ]]', b'[[ position, invariant ]]')
    fixed = re.sub(rb'\.(sample_compare|gather_compare)\(\s*[A-Za-z_][A-Za-z_0-9]*\s*,',
                   rb'.\1(sampler_shadow_linear,', msl)
    if fixed != msl:
        fixed = b'constexpr metal::sampler sampler_shadow_linear(metal::filter::linear, metal::compare_func::less);\n' + fixed
        msl = fixed
    return msl

class Names:
    def __init__(self, pairs):
        self.idx = {n: i for n, i in pairs}
        self.next = max(self.idx.values(), default=-1) + 1
    def __call__(self, name):
        if name not in self.idx:
            self.idx[name] = self.next
            self.next += 1
        return self.idx[name]
    def pairs(self):
        return sorted(self.idx.items())

def tt_params(cbs, binds, N):
    def split(params):
        vec, mat = [], []
        for p in params:
            if p['isMatrix']:
                mat.append(dict(m_NameIndex=N(p['name']), m_Index=p['index'], m_ArraySize=p['arraySize'],
                                m_Type=p['type'], m_RowCount=p['rows']))
            else:
                vec.append(dict(m_NameIndex=N(p['name']), m_Index=p['index'], m_ArraySize=p['arraySize'],
                                m_Type=p['type'], m_Dim=p['cols']))
        return vec, mat

    out = dict(m_VectorParams=[], m_MatrixParams=[], m_TextureParams=[], m_BufferParams=[], m_ConstantBuffers=[],
               m_ConstantBufferBindings=[], m_UAVParams=[], m_Samplers=[])
    for cb in cbs:
        vec, mat = split(cb['params'])
        if cb['name'] == '':
            out['m_VectorParams'], out['m_MatrixParams'] = vec, mat
            continue
        structs = []
        for st in cb['structs']:
            svec, smat = split(st['params'])
            structs.append(dict(m_NameIndex=N(st['name']), m_Index=st['index'], m_ArraySize=st['arraySize'],
                                m_StructSize=st['size'], m_VectorMembers=svec, m_MatrixMembers=smat))
        out['m_ConstantBuffers'].append(dict(m_NameIndex=N(cb['name']), m_MatrixParams=mat, m_VectorParams=vec,
                                             m_StructParams=structs, m_Size=cb['size'], m_IsPartialCB=True))
    for b in binds:
        k = b['kind']
        if k == 'tex':
            out['m_TextureParams'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_SamplerIndex=b['sampler'],
                                               m_MultiSampled=bool(b['extra'] & 1), m_Dim=b['extra'] >> 1))
        elif k == 'cb':
            out['m_ConstantBufferBindings'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_ArraySize=1))
        elif k == 'buf':
            out['m_BufferParams'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_ArraySize=b['arraySize']))
        elif k == 'uav':
            out['m_UAVParams'].append(dict(m_NameIndex=N(b['name']), m_Index=b['index'], m_OriginalIndex=b['orig']))
        elif k == 'sampler':
            out['m_Samplers'].append(dict(sampler=b['sampler'], bindPoint=b['bind']))
    return out

# ---------------- hlslcc batch runner ----------------
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
        if not self.jobs: return
        nproc = max(1, min(nproc or (os.cpu_count() or 2) - 1, len(self.jobs)))
        procs = []
        for i in range(nproc):
            part = self.jobs[i::nproc]
            lf = os.path.join(self.dir, f'list{i}.txt')
            with open(lf, 'w') as f:
                f.writelines(self._line(k) for k in part)
            procs.append(subprocess.Popen([HLSLCC, lf], stderr=subprocess.DEVNULL, stdout=subprocess.DEVNULL))
        for p in procs:
            p.wait()
        for key in self.jobs:
            if not os.path.exists(os.path.join(self.dir, key) + '.json'):
                lf = os.path.join(self.dir, 'single.txt')
                with open(lf, 'w') as f:
                    f.write(self._line(key))
                subprocess.run([HLSLCC, lf], stderr=subprocess.DEVNULL, stdout=subprocess.DEVNULL)

    def result(self, key):
        p = os.path.join(self.dir, key)
        if not os.path.exists(p + '.json'): return None, None
        refl = json.loads(open(p + '.json', 'rb').read().decode('utf8', 'replace'))
        if not refl.get('ok'): return None, refl
        return open(p + '.metal', 'rb').read(), refl

# ---------------- main conversion ----------------
def convert_shader(tt, blob, workdir, log=print):
    if PLATFORM_D3D11 not in tt['platforms']:
        return None
    blob = bytes(blob)
    pi = tt['platforms'].index(PLATFORM_D3D11)
    chunks = platform_chunks(tt, blob, pi)
    ents = entries_of(chunks)
    name = tt['m_ParsedForm']['m_Name']
    batch = HlslccBatch(workdir)
    plan = {}
    stats = dict(ok=0, fail=0, dropped=0, instancing=0)

    for ss in tt['m_ParsedForm']['m_SubShaders']:
        for p in ss['m_Passes']:
            for prog in PROGS:
                pr = p.get(prog)
                if not pr or 'm_SubPrograms' not in pr: continue
                for e in pr['m_SubPrograms']:
                    t = e['m_GpuProgramType']
                    if t not in DX_TYPES: continue
                    if t not in DX_VS | DX_PS:
                        stats['dropped'] += 1
                        continue
                    bi = e['m_BlobIndex']
                    if bi in plan: continue
                    key = f'b{bi}'
                    try:
                        sub = read_sub(get_seg(chunks, ents[bi]))
                        if INSTANCING_KW & set(sub['keywords'] + sub['localKw']):
                            plan[bi] = 'instancing'
                            continue
                        idx2name = {idx: n for n, idx in p['m_NameIndices']}
                        cp = pr.get('m_CommonParameters')
                        P = merge_params(cp, idx2name, dict(cbs=sub['cbs'], binds=sub['binds']))
                        code, _ = prep_dxbc(sub['code'], P)
                        batch.add(key, code)
                        plan[bi] = (key, P, sub, t in DX_VS)
                    except Exception as ex:
                        plan[bi] = None
                        log(f'  [{name}] prep failed: {ex!r}')
    batch.run()

    segs = []
    results = {}
    for bi, item in plan.items():
        if item == 'instancing':
            stats['instancing'] += 1
            continue
        if item is None:
            stats['fail'] += 1
            continue
        key, P, sub, is_vs = item
        msl, refl = batch.result(key)
        if msl is None:
            stats['fail'] += 1
            log(f'  [{name}] hlslcc failed for blob {bi}')
            continue
        try:
            msl = inline_samplers(fix_msl(msl, is_vs), P)
            cb_map = {'VGlobals': '$Globals', 'FGlobals': '$Globals', 'Globals': '$Globals'}
            cbs, binds = build_metal_params(refl, msl, P, cb_map)
            smap, ch = vertex_channels(refl) if is_vs else (0, [])
            msub = dict(version=BLOB_VERSION, type=METAL_VS if is_vs else METAL_FS, stats=list(sub['stats']),
                        keywords=sub['keywords'], localKw=[], code=metal_code(msl, is_vs),
                        sourceMap=smap, channels=ch, cbs=cbs, binds=binds, tail=b'')
            segs.append(write_sub(msub))
            results[bi] = (len(segs) - 1, cbs, binds, ch, smap)
            stats['ok'] += 1
        except Exception as ex:
            stats['fail'] += 1
            log(f'  [{name}] build failed: {ex!r}')

    nt = copy.deepcopy(tt)
    for ss in nt['m_ParsedForm']['m_SubShaders']:
        for p in ss['m_Passes']:
            if p.get('m_Platforms'):
                p['m_Platforms'] = [PLATFORM_METAL] if PLATFORM_D3D11 in p['m_Platforms'] else []
            N = Names(p['m_NameIndices'])
            for prog in PROGS:
                pr = p.get(prog)
                if not pr or 'm_SubPrograms' not in pr: continue
                out = []
                for e in pr['m_SubPrograms']:
                    if e['m_GpuProgramType'] not in DX_VS | DX_PS:
                        continue
                    r = results.get(e['m_BlobIndex'])
                    if r is None:
                        continue
                    mi, cbs, binds, ch, smap = r
                    ne = dict(e)
                    ne['m_Parameters'] = tt_params(cbs, binds, N)
                    ne['m_BlobIndex'] = mi
                    ne['m_GpuProgramType'] = METAL_VS if e['m_GpuProgramType'] in DX_VS else METAL_FS
                    ne['m_ShaderRequirements'] = 1
                    ne['m_Channels'] = dict(m_Channels=[dict(source=s, target=t) for s, t in ch], m_SourceMap=smap)
                    for tier in METAL_TIERS:
                        te = dict(ne)
                        te['m_ShaderHardwareTier'] = tier
                        out.append(te)
                pr['m_SubPrograms'] = out
            p['m_NameIndices'] = N.pairs()
            if stats['instancing']:
                p['m_HasInstancingVariant'] = False
                p['m_HasProceduralInstancingVariant'] = False

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
    nt['stageCounts'] = [tt['stageCounts'][pi]] if tt.get('stageCounts') else []
    nt['compressedBlob'] = b''.join(comp)
    log(f'  [{name}] variants ok={stats["ok"]} fail={stats["fail"]} dropped(non vs/ps)={stats["dropped"]} instancing={stats["instancing"]}')
    return nt, stats
