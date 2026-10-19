"""Convert retail Internet Cafe Simulator 2 (Windows) data so the iOS shell player can load it.

  python tools/convert_data.py [--game ".../Internet Cafe Simulator 2/windows_content"] [--out ../ics2-work]

Produces <out>/Data/ (goes into the app's Data folder, see package_ipa.py). For every serialized file:
  * the target-platform tag is changed from StandaloneWindows64 (19) to iOS (9)
  * every Shader gets its DX11 programs translated to Metal (convert_shader.py, cached in <out>/cache)
  * textures are downscaled / re-encoded as ASTC (transcode_tex.py) and the file's .resS is rebuilt piece by
    piece: every streamed object (textures, meshes) is copied or re-encoded into a new .resS and its offset
    rewritten, so the 4.7 GB sharedassets1.assets.resS is never loaded into memory at once
Audio (.resource) is copied unchanged. The retail install is only read, never modified.
Nothing produced here may be committed to the public repo - it is derived from the game.
"""
import argparse, hashlib, json, os, pickle, re, shutil, struct, sys, time, traceback
from concurrent.futures import ProcessPoolExecutor
from collections import deque

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
import convert_shader
from convert_shader import convert_shader as convert_one
import transcode_tex as tx

UnityPy.config.FALLBACK_UNITY_VERSION = '2020.2.2f1'
IOS_PLATFORM = 9
PLAYER_FILES = ['globalgamemanagers', 'globalgamemanagers.assets', 'level0', 'level1', 'resources.assets',
                'sharedassets0.assets', 'sharedassets1.assets', 'Resources/unity_builtin_extra']
DEFAULT_GAME = r'C:\Program Files (x86)\Steam\steamapps\common\Internet Cafe Simulator 2\windows_content'
DATA_DIR = 'Internet Cafe Simulator 2_Data'
CONVERTER_VERSION = '4'   # bump to invalidate the shader cache
STREAM_ALIGN = 16
IN_FLIGHT = 48            # textures being encoded at once (bounds memory)


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def make_code_header(hdr):
    """Declarative code header from metal_ref.json: {"kind": "prefix", "hex": "<bytes before the MSL source>"}.
    Bytes 40..43 of the prefix are the colour output mask (4 bits per render target): 0x0f for one target,
    0xffff for the 4 G-buffer targets of a deferred pass. It is computed from each fragment program's outputs."""
    if hdr.get('kind') == 'prefix':
        prefix = bytes.fromhex(hdr['hex'])

        def header(msl):
            msl = msl.encode() if isinstance(msl, str) else msl
            m = re.search(rb'struct Mtl_FragmentOut\s*\{(.*?)\};', msl, re.S)
            targets = {int(t) for t in re.findall(rb'SV_Target(\d+)', m.group(1))} if m else set()
            mask = sum(0xf << (4 * t) for t in targets) if targets else 0xf
            return prefix[:40] + struct.pack('<I', mask) + prefix[44:] + msl
        return header
    raise ValueError('unknown code_header kind: %r' % hdr.get('kind'))


def load_metal_conventions(path):
    """Settings measured from the shell's own (Unity-compiled) Metal shaders, see inspect_reference.py."""
    if path and os.path.exists(path):
        ref = json.load(open(path))
        convert_shader.METAL_TIERS = ref.get('tiers', convert_shader.METAL_TIERS)
        hdr = ref.get('code_header')
        if hdr:
            convert_shader.METAL_CODE_HEADER = make_code_header(hdr)
    return convert_shader.METAL_TIERS, convert_shader.METAL_CODE_HEADER


def shader_key(tt, blob):
    h = hashlib.sha1()
    h.update(CONVERTER_VERSION.encode())
    h.update(json.dumps(convert_shader.METAL_TIERS).encode())
    h.update(tt['m_ParsedForm']['m_Name'].encode())
    h.update(blob)
    return h.hexdigest()


def convert_shaders_in(objects, cache_dir, work_dir, report):
    n = 0
    for obj in objects:
        if obj.type.name != 'Shader':
            continue
        tt = obj.read_typetree()
        blob = bytes(tt['compressedBlob'])
        name = tt['m_ParsedForm']['m_Name']
        key = shader_key(tt, blob)
        cp = os.path.join(cache_dir, key + '.pkl')
        if os.path.exists(cp):
            nt, stats = pickle.load(open(cp, 'rb'))
        else:
            tt['compressedBlob'] = b''
            msgs = []
            t0 = time.time()
            try:
                res = convert_one(tt, blob, os.path.join(work_dir, 'hlslcc', key), log=msgs.append)
            except Exception as e:
                report.setdefault('shader_errors', {})[name] = repr(e)
                log('  shader FAILED', name, repr(e)[:200])
                continue
            if res is None:
                report.setdefault('no_dx11', []).append(name)
                continue
            nt, stats = res
            stats = dict(stats, msgs=msgs[:50])
            pickle.dump((nt, stats), open(cp, 'wb'))
            shutil.rmtree(os.path.join(work_dir, 'hlslcc', key), ignore_errors=True)
            log('  shader', name, {k: v for k, v in stats.items() if k != 'msgs'}, '%.0fs' % (time.time() - t0))
        report.setdefault('shaders', {})[name] = {k: v for k, v in stats.items() if k != 'msgs'}
        if stats.get('fail'):
            report.setdefault('shader_msgs', {})[name] = stats.get('msgs', [])
        obj.save_typetree(nt)
        n += 1
    return n


# ------------------------------------------------------------------------------------------------
def patch_stream_ref(obj, sd, new_off):
    """Rewrite only the StreamingInfo offset in an object's raw bytes (no typetree round trip).

    StreamingInfo is the last field of Mesh / Texture2D / Cubemap: UInt64 offset, UInt32 size, string path
    (int32 length + bytes, aligned to 4)."""
    raw = bytearray(obj.get_raw_data())
    path = sd['path'].encode()
    tail = struct.pack('<QI', sd['offset'], sd['size']) + struct.pack('<i', len(path)) + path
    tail += b'\0' * ((-len(tail)) % 4)
    pos = len(raw) - len(tail)
    if pos < 0 or raw[pos:] != tail:
        raise RuntimeError('%s %d: StreamingInfo not at the end of the object' % (obj.type.name, obj.path_id))
    struct.pack_into('<Q', raw, pos, new_off)
    obj.set_raw_data(bytes(raw))


def sprite_textures(data_dir):
    """(file name, path id) of every texture a Sprite draws from. Those keep their size (see transcode_tex)."""
    out = set()
    for f in PLAYER_FILES:
        p = os.path.join(data_dir, f)
        if not os.path.exists(p):
            continue
        env = UnityPy.load(p)
        sf = list(env.files.values())[0]
        externals = [os.path.basename(e.path) for e in sf.externals]
        for obj in sf.objects.values():
            if obj.type.name != 'Sprite':
                continue
            rd = obj.read_typetree().get('m_RD', {})
            for k in ('texture', 'alphaTexture'):
                ref = rd.get(k) or {}
                if not ref.get('m_PathID'):
                    continue
                fid = ref['m_FileID']
                fname = os.path.basename(f) if fid == 0 else externals[fid - 1]
                out.add((fname, ref['m_PathID']))
    return out


def rebuild_streams(sf, fname, src_dir, dst_dir, sprite_tex, pool, report):
    """Copy/re-encode every streamed object of this file into a new .resS; returns (#textures converted)."""
    items = []   # (offset, obj, tt, plan)
    res_name = None
    for obj in sf.objects.values():
        if obj.type.name not in ('Texture2D', 'Cubemap', 'Mesh', 'Texture2DArray', 'Texture3D'):
            continue
        tt = obj.read_typetree()
        sd = tt.get('m_StreamData') or {}
        if obj.type.name in tx.TEXTURE_CLASSES:
            p = tx.plan(tt, (fname, obj.path_id) in sprite_tex)
        else:
            p = None
        if sd.get('size'):
            if res_name is None:
                res_name = sd['path']
            elif sd['path'] != res_name:
                raise RuntimeError('%s: streams into %s and %s' % (fname, res_name, sd['path']))
            items.append((sd['offset'], obj, tt, p))
        elif p is not None:
            # inline image data: convert in place
            data = bytes(tt['image data'])
            enc = tx.encode(p, data)
            tx.apply(tt, p, len(enc))
            tt['image data'] = enc
            obj.save_typetree(tt)
            report['textures_inline'] = report.get('textures_inline', 0) + 1
    if not items:
        return 0
    items.sort(key=lambda x: x[0])
    src_path = os.path.join(src_dir, os.path.basename(res_name))
    dst_path = os.path.join(dst_dir, os.path.basename(res_name))
    if all(p is None for _, _, _, p in items):
        shutil.copyfile(src_path, dst_path)       # nothing to convert: offsets stay valid
        report['resS'] = {'objects': len(items), 'copied': True}
        return 0
    converted = 0
    bytes_in = bytes_out = 0
    t0 = time.time()
    with open(src_path, 'rb') as src, open(dst_path, 'wb') as dst:
        pending = deque()

        def flush(block):
            nonlocal converted, bytes_out
            while pending and (block or pending[0][3] is None or pending[0][3].done()):
                obj, tt, p, fut, raw = pending.popleft()
                if fut is not None:
                    try:
                        data = fut.result()
                        exp = tx.encoded_size(p)
                        if len(data) != exp:
                            raise ValueError('%s: %d bytes, expected %d' % (p['name'], len(data), exp))
                        tx.apply(tt, p, len(data))
                        converted += 1
                    except Exception as e:
                        report.setdefault('texture_errors', []).append('%s: %r' % (p['name'], e))
                        data = raw            # keep the original texture
                else:
                    data = raw
                pad = (-dst.tell()) % STREAM_ALIGN
                if pad:
                    dst.write(b'\0' * pad)
                tt['m_StreamData'] = {'offset': dst.tell(), 'size': len(data), 'path': res_name}
                dst.write(data)
                bytes_out += len(data)
                obj.save_typetree(tt)

        for i, (off, obj, tt, p) in enumerate(items):
            src.seek(off)
            raw = src.read(tt['m_StreamData']['size'])
            bytes_in += len(raw)
            fut = pool.submit(tx.encode, p, raw) if p is not None else None
            pending.append((obj, tt, p, fut, raw))
            while len(pending) > IN_FLIGHT:
                flush(False)
                if len(pending) > IN_FLIGHT:
                    pending[0][3].result()
            if i % 500 == 0:
                log('  %s: %d/%d streamed objects, %d MB in, %d MB out, %.0fs' %
                    (fname, i, len(items), bytes_in >> 20, bytes_out >> 20, time.time() - t0))
        flush(True)
    report['resS'] = {'objects': len(items), 'mb_in': bytes_in >> 20, 'mb_out': bytes_out >> 20}
    return converted


def process_player_file(f, data_dir, out_data, cache_dir, work_dir, sprite_tex, pool):
    report = {}
    src = os.path.join(data_dir, f)
    dst = os.path.join(out_data, f)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    env = UnityPy.load(src)
    sf = list(env.files.values())[0]
    report['shaders_converted'] = convert_shaders_in(sf.objects.values(), cache_dir, work_dir, report)
    report['textures'] = rebuild_streams(sf, os.path.basename(f), os.path.dirname(src), os.path.dirname(dst),
                                         sprite_tex, pool, report)
    sf._m_target_platform = IOS_PLATFORM
    with open(dst, 'wb') as fh:
        fh.write(sf.save())
    # audio next to the file: sharedassets0.assets -> sharedassets0.resource, resources.assets -> resources.resource
    for ext in ('.resource',):
        for base in (src, os.path.splitext(src)[0]):
            if os.path.exists(base + ext):
                shutil.copyfile(base + ext, os.path.join(os.path.dirname(dst), os.path.basename(base) + ext))
    return report


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_GAME)
    ap.add_argument('--out', default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), '..', 'ics2-work'))
    ap.add_argument('--ref', default=None, help='metal_ref.json from inspect_reference.py')
    ap.add_argument('--only', default=None, help='substring filter on file names (testing)')
    ap.add_argument('--jobs', type=int, default=max(1, (os.cpu_count() or 4) - 2))
    ap.add_argument('--max-size', type=int, default=tx.MAX_SIZE)
    a = ap.parse_args()
    tx.MAX_SIZE = a.max_size

    data_dir = os.path.join(a.game, DATA_DIR)
    out = os.path.abspath(a.out)
    out_data = os.path.join(out, 'Data')
    cache_dir = os.path.join(out, 'cache', 'shaders')
    work_dir = os.path.join(out, 'tmp')
    os.makedirs(cache_dir, exist_ok=True)
    ref = a.ref or os.path.join(out, 'metal_ref.json')
    tiers, hdr = load_metal_conventions(ref)
    log('metal tiers', tiers, 'code header', 'yes' if hdr else 'none (raw MSL)', 'max texture size', tx.MAX_SIZE)

    files = [f for f in PLAYER_FILES if os.path.exists(os.path.join(data_dir, f))]
    if a.only:
        files = [f for f in files if a.only in f]
    for f in ('RuntimeInitializeOnLoads.json', 'ScriptingAssemblies.json'):
        shutil.copyfile(os.path.join(data_dir, f), os.path.join(out, 'retail-' + f))

    log('finding sprite textures')
    sprite_tex = sprite_textures(data_dir)
    log(len(sprite_tex), 'textures are used by sprites (kept at full size)')

    report_path = os.path.join(out, 'convert_report.json')
    report = json.load(open(report_path)) if os.path.exists(report_path) and a.only else {}
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=a.jobs) as pool:
        for f in files:
            log('converting', f)
            try:
                rep = process_player_file(f, data_dir, out_data, cache_dir, work_dir, sprite_tex, pool)
            except Exception:
                rep = {'error': traceback.format_exc()}
                log('  ERROR', rep['error'])
            sh = rep.get('shaders', {})
            log('done', f, 'shaders', rep.get('shaders_converted'), 'with failures', sum(1 for s in sh.values() if s.get('fail')),
                'shader errors', len(rep.get('shader_errors', {})), 'textures', rep.get('textures'),
                'texture errors', len(rep.get('texture_errors', [])), rep.get('resS', ''))
            report[f] = rep
            json.dump(report, open(report_path, 'w'), indent=1)
    log('finished in %.0fs' % (time.time() - t0))


if __name__ == '__main__':
    main()
