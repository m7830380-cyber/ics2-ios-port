"""Re-encode the game's desktop textures for iOS: BCn / DXT / crunched / large uncompressed -> ASTC, downscaled.

The retail textures are 5.6 GB (2048-4096 px props, 4k uncompressed flipbooks, ~4200 crunched GIF frames for
the in-game PC). An iPhone can neither hold that in RAM nor ship it in a sane IPA, so every texture is planned
(plan()) and then encoded in a worker process (encode()):

  * size cap: MAX_SIZE px on the longest side. Textures with mipmaps are downscaled by simply dropping their
    top mip levels (no resampling); textures without mipmaps are resized with a box filter.
  * ASTC block: 4x4 up to 256 px, 6x6 in between (and for normal maps), 8x8 at the cap, 10x10 for the GIF frames;
    normal maps are capped at NORMAL_MAX_SIZE.
  * never downscaled: textures used by Sprites (UI Image UVs come from the sprite's pixel rect, which would no
    longer match) and readable textures (scripts may GetPixel them).
  * kept as they are: Alpha8 / single-channel / HDR float formats and anything already mobile-friendly.

Channel layout is preserved (DXT5nm normal maps stay x-in-alpha), because the translated DX11 shaders unpack
them the desktop way.
"""
import math
import astc_encoder
import texture2ddecoder

# Unity TextureFormat ids
ALPHA8, RGB24, RGBA32, ARGB32, BGRA32 = 1, 3, 4, 5, 14
DXT1, DXT5, BC6H, BC7, BC4, BC5, DXT1_CRUNCHED, DXT5_CRUNCHED = 10, 12, 24, 25, 26, 27, 28, 29
ASTC = {4: 48, 5: 49, 6: 50, 8: 51, 10: 52, 12: 53}   # block size -> ASTC_RGBA_NxN (Unity 2020 ids)

BLOCK_BYTES = {DXT1: 8, DXT5: 16, BC6H: 16, BC7: 16, BC4: 8, BC5: 16}
DECODERS = {
    DXT1: texture2ddecoder.decode_bc1,
    DXT5: texture2ddecoder.decode_bc3,
    BC6H: texture2ddecoder.decode_bc6,   # LDR (clamped) decode; only reflection cubemaps use it
    BC7: texture2ddecoder.decode_bc7,
    BC4: texture2ddecoder.decode_bc4,
    BC5: texture2ddecoder.decode_bc5,
}
UNCOMPRESSED_BPP = {RGB24: 3, RGBA32: 4, ARGB32: 4, BGRA32: 4}
TEXTURE_CLASSES = ('Texture2D', 'Cubemap')

MAX_SIZE = 1024
NORMAL_MAX_SIZE = 512   # normal maps: half the albedo size is invisible on a phone screen
QUALITY = 60         # astcenc "medium"
QUALITY_FAST = 20    # GIF frames (thousands of them, low detail)
USAGE_NORMALMAP = 3  # m_LightmapFormat (TextureUsageMode.NormalmapDXT5nm)


def _blocks(n, b=4):
    return max(1, (n + b - 1) // b)


def mip_sizes(w, h, mips):
    out = []
    for _ in range(mips):
        out.append((w, h))
        w, h = max(1, w >> 1), max(1, h >> 1)
    return out


def src_level_bytes(fmt, w, h):
    if fmt in BLOCK_BYTES:
        return _blocks(w) * _blocks(h) * BLOCK_BYTES[fmt]
    return w * h * UNCOMPRESSED_BPP[fmt]


def plan(tt, sprite_texture=False):
    """-> None (keep as is) or a dict describing the conversion of this texture typetree."""
    fmt = tt.get('m_TextureFormat')
    w, h = tt.get('m_Width', 0), tt.get('m_Height', 0)
    if not w or not h:
        return None
    if fmt not in DECODERS and fmt not in UNCOMPRESSED_BPP and fmt not in (DXT1_CRUNCHED, DXT5_CRUNCHED):
        return None
    readable = bool(tt.get('m_IsReadable'))
    if readable and fmt in UNCOMPRESSED_BPP:
        return None                       # scripts may read/write these pixels
    mips = max(1, tt.get('m_MipCount', 1))
    images = max(1, tt.get('m_ImageCount', 1))
    crunched = fmt in (DXT1_CRUNCHED, DXT5_CRUNCHED)
    name = tt.get('m_Name', '')
    gif = crunched and max(w, h) <= 512 and name.startswith('frame_')
    # crunch: only level 0 is decoded; the mip chain is rebuilt from it (except for the GIF frames, UI only)
    gen_mips = crunched and mips > 1 and not gif
    if crunched:
        mips = 1

    normal = tt.get('m_LightmapFormat') == USAGE_NORMALMAP
    cap = min(MAX_SIZE, NORMAL_MAX_SIZE) if normal else MAX_SIZE
    start = 0
    out_w, out_h = w, h
    resize = None
    if not (sprite_texture or readable) and max(w, h) > cap:
        if mips > 1:
            while start < mips - 1 and max(w >> start, h >> start) > cap:
                start += 1
            out_w, out_h = max(1, w >> start), max(1, h >> start)
        else:
            s = cap / max(w, h)
            resize = (max(1, round(w * s)), max(1, round(h * s)))
            out_w, out_h = resize
    out_mips = mips - start
    if gen_mips:
        out_mips = max(out_w, out_h).bit_length()

    m = max(out_w, out_h)
    if gif:
        blk = 10                          # in-game PC screen animations: thousands of small frames
    elif m <= 256:
        blk = 4
    elif sprite_texture:
        blk = 4 if m <= 512 else 6        # UI: keep text and icons crisp
    elif m >= MAX_SIZE and not normal:
        blk = 8
    else:
        blk = 6
    return dict(fmt=fmt, w=w, h=h, mips=mips, images=images, start=start, resize=resize,
                out_w=out_w, out_h=out_h, out_mips=out_mips, gen_mips=gen_mips, block=blk,
                quality=QUALITY_FAST if gif else QUALITY, name=name)


def apply(tt, p, size):
    """Update a texture typetree for the encoded data (size bytes, streamed)."""
    tt['m_Width'], tt['m_Height'] = p['out_w'], p['out_h']
    tt['m_MipCount'] = p['out_mips']
    tt['m_TextureFormat'] = ASTC[p['block']]
    tt['m_CompleteImageSize'] = size // p['images']
    tt['image data'] = b''
    if 'm_PlatformBlob' in tt:
        tt['m_PlatformBlob'] = []
    if 'm_MipsStripped' in tt:
        tt['m_MipsStripped'] = 0


# ------------------------------------------------------------------------------------------------
# worker side
_ctx = {}


def _context(block, quality):
    k = (block, quality)
    if k not in _ctx:
        cfg = astc_encoder.ASTCConfig(astc_encoder.ASTCProfile.LDR, block, block, block_z=1, quality=quality,
                                      flags=astc_encoder.ASTCConfigFlags.USE_DECODE_UNORM8)
        _ctx[k] = astc_encoder.ASTCContext(cfg)
    return _ctx[k]


def _bgra_to_rgba(buf):
    b = bytearray(buf)
    b[0::4], b[2::4] = b[2::4], b[0::4]
    return bytes(b)


def _crop(rgba, pw, w, h):
    if pw == w:
        return rgba[:w * h * 4]
    row = pw * 4
    return b''.join(rgba[y * row:y * row + w * 4] for y in range(h))


def _decode(fmt, data, w, h):
    """One mip level -> tightly packed RGBA8 of exactly w x h."""
    if fmt in DECODERS:
        pw, ph = _blocks(w) * 4, _blocks(h) * 4
        return _crop(_bgra_to_rgba(DECODERS[fmt](data, pw, ph)), pw, w, h)
    if fmt == RGBA32:
        return bytes(data[:w * h * 4])
    if fmt == BGRA32:
        return _bgra_to_rgba(data[:w * h * 4])
    if fmt == ARGB32:
        b = bytearray(data[:w * h * 4])
        out = bytearray(len(b))
        out[0::4], out[1::4], out[2::4], out[3::4] = b[1::4], b[2::4], b[3::4], b[0::4]
        return bytes(out)
    if fmt == RGB24:
        src = data[:w * h * 3]
        out = bytearray(w * h * 4)
        out[0::4], out[1::4], out[2::4] = src[0::3], src[1::3], src[2::3]
        out[3::4] = b'\xff' * (w * h)
        return bytes(out)
    raise ValueError('format %d' % fmt)


def _resize(rgba, w, h, nw, nh):
    from PIL import Image
    return Image.frombytes('RGBA', (w, h), rgba).resize((nw, nh), Image.BOX).tobytes()


def encode(p, data):
    """Worker: source bytes (all images, all source mips) -> ASTC bytes (all images, output mips)."""
    fmt = p['fmt']
    ctx = _context(p['block'], p['quality'])
    swz = astc_encoder.ASTCSwizzle.from_str('RGBA')

    def enc(rgba, w, h):
        return ctx.compress(astc_encoder.ASTCImage(astc_encoder.ASTCType.U8, w, h, 1, rgba), swz)

    if fmt in (DXT1_CRUNCHED, DXT5_CRUNCHED):
        dxt = texture2ddecoder.unpack_unity_crunch(bytes(data))
        base = DXT1 if fmt == DXT1_CRUNCHED else DXT5
        rgba = _decode(base, dxt[:src_level_bytes(base, p['w'], p['h'])], p['w'], p['h'])
        w, h = p['w'], p['h']
        out = bytearray()
        for lvl, (mw, mh) in enumerate(mip_sizes(p['out_w'], p['out_h'], p['out_mips'])):
            if (mw, mh) != (w, h):
                rgba, w, h = _resize(rgba, w, h, mw, mh), mw, mh
            out += enc(rgba, w, h)
        return bytes(out)

    sizes = mip_sizes(p['w'], p['h'], p['mips'])
    per_image = sum(src_level_bytes(fmt, mw, mh) for mw, mh in sizes)
    if len(data) < per_image * p['images']:
        raise ValueError('%s: %d bytes < %d x %d' % (p['name'], len(data), p['images'], per_image))
    out = bytearray()
    for img in range(p['images']):
        pos = img * per_image
        for lvl, (mw, mh) in enumerate(sizes):
            n = src_level_bytes(fmt, mw, mh)
            if lvl >= p['start']:
                rgba = _decode(fmt, data[pos:pos + n], mw, mh)
                if p['resize']:
                    nw, nh = p['resize']
                    rgba, mw, mh = _resize(rgba, mw, mh, nw, nh), nw, nh
                out += enc(rgba, mw, mh)
            pos += n
    return bytes(out)


def encoded_size(p):
    """Expected ASTC byte count (sanity check)."""
    b = p['block']
    total = sum(_blocks(w, b) * _blocks(h, b) * 16 for w, h in mip_sizes(p['out_w'], p['out_h'], p['out_mips']))
    return total * p['images']
