"""Build the final InternetCafeSim2.ipa from the CI shell IPA plus the converted game data.

  python tools/package_ipa.py [--work ../ics2-work] [--shell path/to/ICS2-shell.ipa]

Without --shell the `shell-latest` release of the PRIVATE repo is downloaded with the GitHub CLI.
The IPA is written directly from the shell zip and <work>/Data (no unpacked staging copy: the PC has
little free disk space). Output: <work>/InternetCafeSim2.ipa (install with TrollStore). Never upload it
anywhere public.
"""
import argparse, json, os, subprocess, zipfile

PRIVATE_REPO = 'm7830380-cyber/ics2-ios-private'
SHELL_ASSET = 'ICS2-shell.ipa'
# Player data files that come from the retail game (converted). Everything else in Data/ (IL2CPP
# metadata, "unity default resources", ScriptingAssemblies.json, ...) is the iOS shell's own.
RETAIL_PREFIXES = ('level', 'sharedassets', 'resources.', 'globalgamemanagers', 'Resources/unity_builtin_extra')
# already-compressed or huge binary data: store, don't deflate
STORE_EXT = ('.resS', '.resource')


def merge_rtiol(shell_json, retail_json):
    """Union of RuntimeInitializeOnLoads.json from retail and shell (the shell adds ICS2iOS)."""
    shell = json.loads(shell_json) if shell_json else {'root': []}
    if not os.path.exists(retail_json):
        return json.dumps(shell)
    retail = json.load(open(retail_json))
    seen = {(e['assemblyName'], e['nameSpace'], e['className'], e['methodName']) for e in shell['root']}
    added = 0
    for e in retail['root']:
        k = (e['assemblyName'], e['nameSpace'], e['className'], e['methodName'])
        if k not in seen:
            shell['root'].append(e)
            seen.add(k)
            added += 1
    print('RuntimeInitializeOnLoads: %d entries (+%d from retail)' % (len(shell['root']), added))
    return json.dumps(shell)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), '..', 'ics2-work'))
    ap.add_argument('--shell')
    ap.add_argument('--out', default=None)
    a = ap.parse_args()
    work = os.path.abspath(a.work)

    shell = a.shell
    if not shell:
        shell = os.path.join(work, SHELL_ASSET)
        if os.path.exists(shell):
            os.remove(shell)
        subprocess.check_call(['gh', 'release', 'download', 'shell-latest', '--repo', PRIVATE_REPO,
                               '-p', SHELL_ASSET, '-D', work])

    out = a.out or os.path.join(work, 'InternetCafeSim2.ipa')
    tmp = out + '.part'
    conv = os.path.join(work, 'Data')
    with zipfile.ZipFile(shell) as zin, zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED, allowZip64=True, compresslevel=6) as zout:
        app_data = next(n for n in zin.namelist() if n.startswith('Payload/') and n.endswith('.app/Data/Managed/Metadata/global-metadata.dat'))
        app_data = app_data[:-len('Managed/Metadata/global-metadata.dat')]
        print('shell data dir:', app_data)
        skipped = 0
        for info in zin.infolist():
            rel = info.filename[len(app_data):] if info.filename.startswith(app_data) else None
            if rel is not None and rel.startswith(RETAIL_PREFIXES):
                skipped += 1
                continue
            data = zin.read(info)
            if rel == 'RuntimeInitializeOnLoads.json':
                data = merge_rtiol(data.decode('utf-8'), os.path.join(work, 'retail-RuntimeInitializeOnLoads.json')).encode()
            zout.writestr(info, data)
        print('shell files copied, %d placeholder data files dropped' % skipped)
        copied = 0
        for root, _, files in os.walk(conv):
            for f in files:
                src = os.path.join(root, f)
                arc = app_data + os.path.relpath(src, conv).replace('\\', '/')
                ct = zipfile.ZIP_STORED if f.endswith(STORE_EXT) else zipfile.ZIP_DEFLATED
                zout.write(src, arc, compress_type=ct)
                copied += 1
        print('copied %d converted data files' % copied)
    os.replace(tmp, out)
    print('wrote', out, os.path.getsize(out) >> 20, 'MB')


if __name__ == '__main__':
    main()
