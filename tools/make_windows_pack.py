"""Make the Windows co-op pack: the patched game assemblies + the mod + the installer, as one zip.

  python tools/make_windows_pack.py --build build-5 [--out ../ics2-work/ICS2-Coop-Windows-build-5.zip]

The pack holds game code (patched assemblies): it goes to the PRIVATE repo's releases only. The installer
(windows/) builds a co-op copy from the player's own Steam install; no game data is in the pack.
"""
import argparse, hashlib, json, os, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLUGINS = os.path.join(os.path.dirname(ROOT), 'ics2-ios-private', 'Plugins')
GAME_ASM = (r'C:\Program Files (x86)\Steam\steamapps\common\Internet Cafe Simulator 2\windows_content'
            r'\Internet Cafe Simulator 2_Data\Managed\Assembly-CSharp.dll')
WINDOWS = os.path.join(ROOT, 'windows')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--build', required=True)
    ap.add_argument('--out')
    ap.add_argument('--plugins', default=PLUGINS)
    a = ap.parse_args()
    out = a.out or os.path.join(os.path.dirname(ROOT), 'ics2-work', f'ICS2-Coop-Windows-{a.build}.zip')
    manifest = {
        'build': a.build,
        'game_assembly_sha256': hashlib.sha256(open(GAME_ASM, 'rb').read()).hexdigest(),
    }
    top = 'ICS2-Coop-Windows'
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        for f in sorted(os.listdir(WINDOWS)):
            z.write(os.path.join(WINDOWS, f), f'{top}/{f}')
        z.writestr(f'{top}/manifest.json', json.dumps(manifest, indent=1))
        n = 0
        for f in sorted(os.listdir(a.plugins)):
            if f.endswith('.dll'):
                z.write(os.path.join(a.plugins, f), f'{top}/Managed/{f}')
                n += 1
    print(f'{out}: {n} assemblies, {os.path.getsize(out) / 1e6:.1f} MB')


if __name__ == '__main__':
    main()
