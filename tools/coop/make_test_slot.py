"""Make (or refresh) a Windows test install of the game with the patched assemblies and the mod.

  python tools/coop/make_test_slot.py [--slot 1] [--root %USERPROFILE%/ics2-test]

The store install is never written to. The slot hardlinks every big file of it (no extra disk space; the
game never writes into its install folder) and only copies what changes:
  * Data/Managed: retail assemblies overlaid with the patched ones + ICS2Shim + ICS2iOS (build_plugins.py output)
  * ScriptingAssemblies.json / RuntimeInitializeOnLoads.json: list the mod so Unity loads and starts it
  * app.info / globalgamemanagers: product name changed (same length) so the slot's PlayerPrefs (registry) and
    default data folder are separate from the owner's game. Saves go to -ics2profile anyway.
"""
import argparse, json, os, shutil, sys

GAME = r'C:\Program Files (x86)\Steam\steamapps\common\Internet Cafe Simulator 2\windows_content'
DATA = 'Internet Cafe Simulator 2_Data'
PRODUCT = b'Internet Cafe Simulator 2'
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PLUGINS = os.path.join(os.path.dirname(ROOT), 'ics2-ios-private', 'Plugins')
COPIED = {'app.info', 'globalgamemanagers', 'ScriptingAssemblies.json', 'RuntimeInitializeOnLoads.json', 'boot.config'}
MOD_ASSEMBLIES = ['ICS2Shim.dll', 'ICS2iOS.dll']
# [RuntimeInitializeOnLoadMethod]s of ICS2iOS (loadTypes: 1 = BeforeSceneLoad, 4 = SubsystemRegistration)
MOD_INIT = [('ICS2iOS', 'Bootstrap', 'EarlyInit', 4), ('ICS2iOS', 'Bootstrap', 'Init', 1)]


def product_name(slot):
    name = f'ICS2 Coop Test Install {slot:02d}'.encode()
    assert len(name) == len(PRODUCT), name
    return name


def link_or_copy(src, dst):
    if os.path.exists(dst):
        if os.path.samefile(src, dst):
            return
        os.remove(dst)
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--slot', type=int, default=1)
    ap.add_argument('--root', default=os.path.join(os.path.expanduser('~'), 'ics2-test'))
    ap.add_argument('--game', default=GAME)
    ap.add_argument('--plugins', default=PLUGINS)
    a = ap.parse_args()
    slot_dir = os.path.join(a.root, f'slot{a.slot}')
    game = os.path.abspath(a.game)
    managed_rel = os.path.join(DATA, 'Managed')

    linked = copied = 0
    for dirpath, _, files in os.walk(game):
        rel_dir = os.path.relpath(dirpath, game)
        os.makedirs(os.path.join(slot_dir, rel_dir), exist_ok=True)
        for f in files:
            src = os.path.join(dirpath, f)
            dst = os.path.join(slot_dir, rel_dir, f)
            if rel_dir == managed_rel or (rel_dir == DATA and f in COPIED):
                if os.path.exists(dst):
                    os.remove(dst)  # never write through a hardlink into the store install
                shutil.copy2(src, dst)
                copied += 1
            else:
                link_or_copy(src, dst)
                linked += 1

    data = os.path.join(slot_dir, DATA)
    managed = os.path.join(data, 'Managed')
    n = 0
    for f in os.listdir(a.plugins):
        if f.endswith('.dll'):
            shutil.copyfile(os.path.join(a.plugins, f), os.path.join(managed, f))
            n += 1

    p = os.path.join(data, 'ScriptingAssemblies.json')
    j = json.load(open(p, encoding='utf-8'))
    for asm in MOD_ASSEMBLIES:
        if asm not in j['names']:
            j['names'].append(asm)
            j['types'].append(16)
    json.dump(j, open(p, 'w', encoding='utf-8'), separators=(',', ':'))

    p = os.path.join(data, 'RuntimeInitializeOnLoads.json')
    j = json.load(open(p, encoding='utf-8'))
    have = {(e['assemblyName'], e['className'], e['methodName']) for e in j['root']}
    for asm, cls, method, load in MOD_INIT:
        if (asm, cls, method) not in have:
            j['root'].append({'assemblyName': asm, 'nameSpace': 'ICS2iOS', 'className': cls, 'methodName': method,
                              'loadTypes': load, 'isUnityClass': False})
    json.dump(j, open(p, 'w', encoding='utf-8'), separators=(',', ':'))

    name = product_name(a.slot)
    for f in ('app.info', 'globalgamemanagers'):
        p = os.path.join(data, f)
        b = open(p, 'rb').read()
        count = b.count(PRODUCT)
        if count == 0:
            sys.exit(f'{f}: product name not found')
        open(p, 'wb').write(b.replace(PRODUCT, name))
        print(f'{f}: product name replaced {count}x')

    exe = os.path.join(slot_dir, 'Internet Cafe Simulator 2.exe')
    print(f'slot {a.slot}: {linked} hardlinked, {copied} copied, {n} patched assemblies -> {slot_dir}')
    print(f'run: "{exe}" -screen-fullscreen 0 -screen-width 960 -screen-height 540 -ics2profile <dir>')


if __name__ == '__main__':
    main()
