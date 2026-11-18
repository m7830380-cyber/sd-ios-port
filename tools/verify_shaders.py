"""Static checks for Metal Shader typetrees (converted ones, or Unity's own as a reference).

  python tools/verify_shaders.py <unity file> [...]      (e.g. work/Data/sharedassets0.assets)

For every Metal subprogram the MSL source is parsed and compared with the parameter table in its blob segment:
  * each constant buffer struct in the MSL: member offsets by Metal layout rules == the segment's param offsets,
    struct size <= the segment's cbuffer size, buffer(n) slot == the segment's cb binding;
  * every texture(n) / sampler(n) / attribute(n) in the entry point has a matching binding / vertex channel;
  * one named cbuffer has the same layout in every program that uses it (stages of a pass, and all shaders);
  * the code header is the one Unity 2021.2 writes (0xF00DCAFE, version 5, flags 5).
"""
import re, sys, os, struct, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import convert_shader as C

SCALAR = {'float': 4, 'int': 4, 'uint': 4, 'half': 2, 'short': 2, 'ushort': 2, 'bool': 1}


def type_layout(t, structs):
    t = t.strip()
    if t in structs:
        return structs[t]['size'], structs[t]['align']
    m = re.match(r'(packed_)?(float|int|uint|half|short|ushort|bool)(\d)?(?:x(\d))?$', t)
    if not m:
        raise ValueError('unknown type ' + t)
    packed, base, n, cols = m.group(1), SCALAR[m.group(2)], int(m.group(3) or 1), m.group(4)
    if cols:      # matrix: cols columns of n-vectors
        cs, ca = type_layout('%s%d' % (m.group(2), n), structs)
        return cs * int(cols), ca
    if packed:
        return base * n, base
    a = base * (4 if n == 3 else n)
    return a, a


def parse_structs(msl):
    """-> {type name: dict(size, align, members=[(name, offset, size, elemcount)])} in declaration order."""
    out = {}
    for m in re.finditer(r'struct (\w+)\s*\{([^}]*)\}', msl):
        name, body = m.group(1), m.group(2)
        if name.startswith('Mtl_'):
            continue
        off, align, mem = 0, 1, []
        for line in body.split(';'):
            line = line.strip()
            if not line:
                continue
            line = re.sub(r'\[\[.*?\]\]', '', line).strip()
            mm = re.match(r'([\w:]+)\s+(\w+)(?:\[(\d+)\])?$', line)
            if not mm:
                raise ValueError('cannot parse member %r of %s' % (line, name))
            sz, al = type_layout(mm.group(1), out)
            cnt = int(mm.group(3) or 1)
            off = (off + al - 1) // al * al
            mem.append((mm.group(2), off, sz * cnt, cnt))
            off += sz * cnt
            align = max(align, al)
        out[name] = dict(size=(off + align - 1) // align * align, align=align, members=mem)
    return out


ARG = re.compile(r'(constant|device|const constant|const device)?\s*([\w:<>, ]+?)\s*[&*]?\s*(\w+)\s*\[\[\s*(buffer|texture|sampler)\s*\((\d+)\)\s*\]\]')


def check_sub(sub, issues, where, layouts):
    code = sub['code']
    if code[:4] != struct.pack('<I', 0xF00DCAFE):
        issues.append((where, 'no MetalShaderBlob header'))
        return
    h = struct.unpack_from('<IIIIIIIIIiiI', code, 0)
    if (h[1], h[2], h[9]) != (48, 5, 5):
        issues.append((where, 'code header %r' % (h[:12],)))
    msl = code[h[3]:].decode('utf8', 'replace')
    try:
        structs = parse_structs(msl)
    except ValueError as ex:
        issues.append((where, str(ex)))
        return
    entry = re.search(r'(vertex|fragment)\s+\w+\s+xlatMtlMain\s*\((.*?)\)\s*\{', msl, re.S)
    if not entry:
        issues.append((where, 'no entry point'))
        return
    args = entry.group(2)
    cbs = {cb['name']: cb for cb in sub['cbs'] if cb['name']}
    binds = collections.defaultdict(dict)
    for b in sub['binds']:
        binds[b['kind']][b['name']] = b
    inline_slots = {b['bind'] for b in sub['binds'] if b['kind'] == 'sampler'}
    tex_sampler_slots = {b['sampler'] for b in sub['binds'] if b['kind'] == 'tex'}
    for m in ARG.finditer(args):
        typ, name, kind, slot = m.group(2), m.group(3), m.group(4), int(m.group(5))
        if kind == 'buffer' and typ.endswith('_Type') and (m.group(1) or '').endswith('constant'):
            st = structs.get(typ.split()[-1])
            cb = cbs.get(name)
            if cb is None:
                issues.append((where, 'cbuffer %s has no parameters' % name))
                continue
            b = binds['cb'].get(name)
            if b is None or b['index'] != slot:
                issues.append((where, 'cbuffer %s bound at %s, MSL buffer(%d)' % (name, b and b['index'], slot)))
            if st is None:
                continue
            if st['size'] > cb['size']:
                issues.append((where, 'cbuffer %s: struct %d bytes > declared %d' % (name, st['size'], cb['size'])))
            mem = {n: o for n, o, _, _ in st['members']}
            for p in cb['params']:
                mn = p['name'] if p['name'] in mem else 'hlslcc_mtx4x4' + p['name']
                if mn not in mem:
                    mn = next((k for k in mem if k.startswith('hlslcc_mtx') and k.endswith(p['name'])), None)
                if mn is None:
                    issues.append((where, '%s.%s not in the MSL struct' % (name, p['name'])))
                elif mem[mn] != p['index']:
                    issues.append((where, '%s.%s at %d, MSL struct has it at %d' % (name, p['name'], p['index'], mem[mn])))
            for s_ in cb['structs']:
                if s_['name'] not in mem or mem[s_['name']] != s_['index']:
                    issues.append((where, '%s.%s (struct) at %d, MSL %s' % (name, s_['name'], s_['index'], mem.get(s_['name']))))
            if not name.endswith('Globals'):
                sig = (cb['size'], tuple(sorted((p['name'], p['index']) for p in cb['params'])))
                layouts[name].setdefault(sig, where)
        elif kind == 'texture':
            b = binds['tex'].get(name) or binds['uav'].get(name)
            if b is None:
                issues.append((where, 'texture %s not bound' % name))
            elif b['index'] != slot:
                issues.append((where, 'texture %s at %d, MSL texture(%d)' % (name, b['index'], slot)))
        elif kind == 'sampler':
            if slot not in inline_slots and slot not in tex_sampler_slots:
                issues.append((where, 'sampler %s (slot %d) has no texture/inline binding' % (name, slot)))
    if sub['type'] == C.METAL_VS:
        attrs = {int(a) for a in re.findall(r'\[\[\s*attribute\((\d+)\)\s*\]\]', msl)}
        targets = {t - 13 for _, t in sub['channels']}
        if attrs != targets:
            issues.append((where, 'vertex attributes %s, channels %s' % (sorted(attrs), sorted(targets))))
    if 'compare_func::less' in msl and 'sample_compare' in msl:
        issues.append((where, 'shadow sampler with compare_func::less (Unity Metal is reversed-Z: greater_equal)'))


def with_common(sub, common, names):
    """Segment params + the stage's typetree m_CommonParameters (Unity's own shaders share some there)."""
    if not common or not any(common[k] for k in common):
        return sub
    sub = dict(sub, cbs=[dict(cb, params=list(cb['params']), structs=list(cb['structs'])) for cb in sub['cbs']],
               binds=list(sub['binds']))
    for cb in common['m_ConstantBuffers']:
        cn = names[cb['m_NameIndex']]
        t = next((c for c in sub['cbs'] if c['name'] == cn), None)
        if t is None:
            t = dict(name=cn, size=cb['m_Size'], params=[], structs=[])
            sub['cbs'].append(t)
        t['size'] = max(t['size'], cb['m_Size'])
        t['params'] += [dict(name=names[m['m_NameIndex']], index=m['m_Index']) for m in cb['m_MatrixParams'] + cb['m_VectorParams']]
        t['structs'] += [dict(name=names[m['m_NameIndex']], index=m['m_Index']) for m in cb['m_StructParams']]
    for b in common['m_ConstantBufferBindings']:
        sub['binds'].append(dict(kind='cb', name=names[b['m_NameIndex']], index=b['m_Index']))
    for t in common['m_TextureParams']:
        sub['binds'].append(dict(kind='tex', name=names[t['m_NameIndex']], index=t['m_Index'], sampler=t['m_SamplerIndex']))
    for b in common['m_BufferParams']:
        sub['binds'].append(dict(kind='buf', name=names[b['m_NameIndex']], index=b['m_Index']))
    for u in common['m_UAVParams']:
        sub['binds'].append(dict(kind='uav', name=names[u['m_NameIndex']], index=u['m_Index']))
    for sm in common['m_Samplers']:
        sub['binds'].append(dict(kind='sampler', name='', bind=sm['bindPoint'], sampler=sm['sampler']))
    return sub


def check_shader(tt, issues, layouts, stats):
    name = tt['m_ParsedForm']['m_Name']
    if C.PLATFORM_METAL not in tt['platforms']:
        return
    pi = tt['platforms'].index(C.PLATFORM_METAL)
    chunks = C.platform_chunks(tt, bytes(tt['compressedBlob']), pi)
    ents = C.entries_of(chunks)
    seen = {}
    for si, ss in enumerate(tt['m_ParsedForm']['m_SubShaders']):
        for pa, p in enumerate(ss['m_Passes']):
            names = {v: k for k, v in p['m_NameIndices']}
            for prog in ('progVertex', 'progFragment'):
                tiers = collections.Counter()
                seen = {}
                for e in p[prog]['m_SubPrograms']:
                    tiers[e['m_ShaderHardwareTier']] += 1
                    if any(e['m_Parameters'][k] for k in e['m_Parameters']):
                        stats['typetree m_Parameters non-empty'] += 1
                    bi = e['m_BlobIndex']
                    if bi in seen:
                        continue
                    sub = with_common(C.read_sub(C.get_seg(chunks, ents[bi])), p[prog]['m_CommonParameters'], names)
                    seen[bi] = sub
                    stats['programs'] += 1
                    if sub['type'] != e['m_GpuProgramType']:
                        issues.append(((name, si, pa, prog), 'entry type %d, segment type %d' % (e['m_GpuProgramType'], sub['type'])))
                    check_sub(sub, issues, (name, si, pa, prog, tuple(sub['keywords'])), layouts)
                if len(set(tiers.values())) > 1:
                    issues.append(((name, si, pa, prog), 'tiers uneven %s' % dict(tiers)))


def verify_env(objs):
    issues, layouts, stats = [], collections.defaultdict(dict), collections.Counter()
    for tt in objs:
        check_shader(tt, issues, layouts, stats)
    for cn, sigs in layouts.items():
        if len(sigs) > 1:
            issues.append(((cn,), 'named cbuffer has %d different layouts, e.g. %s' % (len(sigs), list(sigs.values())[:2])))
    return issues, stats


def main():
    import UnityPy
    objs = []
    for f in sys.argv[1:]:
        env = UnityPy.load(f)
        objs += [o.read_typetree() for o in env.objects if o.type.name == 'Shader']
    issues, stats = verify_env(objs)
    kinds = collections.Counter(re.sub(r'\d+', 'N', re.sub(r'\w+\.\w+', 'X.Y', msg))[:70] for _, msg in issues)
    print('shaders %d, %s' % (len(objs), dict(stats)))
    print('issues %d' % len(issues))
    for k, n in kinds.most_common(30):
        print('  %5d  %s' % (n, k))
    for w, msg in issues[:40]:
        print('  ', w, msg)
    return 1 if issues else 0


if __name__ == '__main__':
    sys.exit(main())
