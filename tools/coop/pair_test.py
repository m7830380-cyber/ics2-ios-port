"""Two real game copies in co-op: a host and a guest on one PC, no bot in between.

  python tools/coop/pair_test.py [--no-build] [--keep]

The games are driven through their test file channel (-ics2test: report.txt written every second,
commands.txt executed). Checks, in order: the guest joins and matches the host's world; each sees the other's
body where the other is; money changes on either side reach both; clocks agree; an item picked up and built
by one is carried and placed on the other (both directions); the town's people stand in the same places;
the panel draws; when the guest quits, the host drops its body; the guest's own save never changed.
Prints one RESULT line. Needs test slots 1 and 2 (make_test_slot.py) and the snapshot fixture (run_test.py
snapshot) for the guest's own save.
"""
import argparse, hashlib, math, os, shutil, subprocess, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import run_test as rt  # noqa: E402

PROFILES = os.path.join(rt.TEST_ROOT, 'profiles')


class Fail(Exception):
    pass


def report(profile):
    p = os.path.join(profile, 'report.txt')
    try:
        text = open(p, encoding='utf-8').read()
    except OSError:
        return {}
    d = {}
    for line in text.splitlines():
        if '=' in line:
            k, v = line.split('=', 1)
            d[k] = v
    return d


def wait(profile, pred, timeout, what):
    deadline = time.time() + timeout
    last = {}
    while time.time() < deadline:
        last = report(profile)
        try:
            if last and pred(last):
                print(f'  ok: {what}', flush=True)
                return last
        except (KeyError, ValueError, IndexError):
            pass
        time.sleep(0.5)
    raise Fail(f'timed out: {what} (last: {dict(list(last.items())[:30])})')


def command(profile, line):
    with open(os.path.join(profile, 'commands.txt'), 'a', encoding='utf-8') as f:
        f.write(line + '\n')


def detail(profile, text):
    open(os.path.join(profile, 'report-detail.txt'), 'w', encoding='utf-8').write(text)


def vec(s):
    return [float(x) for x in s.split(',')]


def dist(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def launch(slot, profile, name, extra):
    exe = os.path.join(rt.TEST_ROOT, f'slot{slot}', rt.EXE)
    args = [exe, '-screen-fullscreen', '0', '-screen-width', '640', '-screen-height', '360',
            '-ics2profile', profile, '-ics2test', '-ics2name', name, '-logFile', os.path.join(profile, 'player.log')] + extra
    return subprocess.Popen(args)


def free_mb():
    try:
        out = subprocess.run(['wmic', 'OS', 'get', 'FreePhysicalMemory'], capture_output=True, text=True).stdout
        return int(out.split()[1]) // 1024
    except Exception:
        return -1


def money(r):
    return float(r['game.money'])


def run(keep):
    hp, gp = os.path.join(PROFILES, 'pair-host'), os.path.join(PROFILES, 'pair-guest')
    for p in (hp, gp):
        if os.path.isdir(p):
            shutil.rmtree(p)
        os.makedirs(p)
    seed = os.path.join(rt.FIXTURES, 'own-save.save')
    if not os.path.exists(seed):
        raise Fail('no own-save fixture: run "run_test.py snapshot" first')
    shutil.copyfile(seed, os.path.join(gp, rt.OWN_SAVE))
    seed_hash = rt.sha(seed)
    procs = []
    try:
        print(f'free memory before: {free_mb()} MB', flush=True)
        host = launch(1, hp, 'Host', ['-ics2autostart', '-ics2coop', 'host'])
        procs.append(host)
        h = wait(hp, lambda r: r['scene'] == 'MainScene' and r['coop.role'] == 'host' and r['game.ready'] == 'True', 180, 'host in its cafe, hosting')
        guest = launch(2, gp, 'Guest', ['-ics2coop', 'join', '127.0.0.1'])
        procs.append(guest)
        g = wait(gp, lambda r: r['scene'] == 'MainScene' and r['coop.role'] == 'client' and r['items.ready'] == 'True'
                 and r['money.synced'] == 'True', 240, 'guest in the host\'s cafe')
        print(f'free memory with both games: {free_mb()} MB', flush=True)
        if g['save.file'] != 'coop-session.save':
            raise Fail('guest plays in ' + g['save.file'])
        if g['items.missing'] != '0' or g['items.extra'] != '0':
            raise Fail(f'guest world differs: missing {g["items.missing"]}, extra {g["items.extra"]}')

        # bodies
        h = wait(hp, lambda r: r['puppets'] == '1' and 'puppet.1.pos' in r, 20, 'host sees the guest')
        g = wait(gp, lambda r: r['puppets'] == '1' and 'puppet.0.pos' in r, 20, 'guest sees the host')
        e1 = dist(vec(h['puppet.1.pos']), vec(g['player.pos']))
        e2 = dist(vec(g['puppet.0.pos']), vec(h['player.pos']))
        print(f'  bodies: guest shown {e1:.2f} m off on the host, host shown {e2:.2f} m off on the guest')
        if e1 > 0.6 or e2 > 0.6:
            raise Fail('a body is not where its player is')

        # money both ways
        m0 = money(h)
        command(gp, 'money -100')
        wait(hp, lambda r: abs(money(r) - (m0 - 100)) < 0.01, 10, 'guest spent 100: host money')
        command(hp, 'money 250')
        wait(gp, lambda r: abs(money(r) - (m0 + 150)) < 0.01, 10, 'host earned 250: guest money')
        h, g = report(hp), report(gp)
        dh = abs(float(h['time.hourf']) - float(g['time.hourf']))
        print(f'  clocks: host {h["time.hourf"]} guest {g["time.hourf"]}')
        if min(dh, 24 - dh) > 0.1:
            raise Fail('clocks differ by %.2f h' % dh)

        # an item, host -> guest, then guest -> host
        iid = h['items.any']
        detail(hp, f'items={iid};civs=0,1,2,3,4,5')
        detail(gp, f'items={iid};civs=0,1,2,3,4,5')
        h = wait(hp, lambda r: f'item.{iid}.pos' in r, 5, f'host item {iid}')
        p0 = vec(h[f'item.{iid}.pos'])
        command(hp, f'take {iid}')
        wait(gp, lambda r: r[f'item.{iid}.held'] == '0', 10, 'guest sees the host carrying it')
        time.sleep(1)
        p1 = [p0[0] + 0.3, p0[1], p0[2]]
        command(hp, 'build ' + ','.join(f'{x:.3f}' for x in p1))
        wait(gp, lambda r: r[f'item.{iid}.held'] == '-1' and dist(vec(r[f'item.{iid}.pos']), p1) < 0.1, 10, 'guest sees it built where the host put it')
        command(gp, f'take {iid}')
        wait(hp, lambda r: r[f'item.{iid}.held'] == '1', 10, 'host sees the guest carrying it')
        time.sleep(1)
        p2 = [p0[0] - 0.3, p0[1], p0[2]]
        command(gp, 'build ' + ','.join(f'{x:.3f}' for x in p2))
        h = wait(hp, lambda r: r[f'item.{iid}.held'] == '-1' and dist(vec(r[f'item.{iid}.pos']), p2) < 0.1, 10, 'host has it built where the guest put it')
        if h[f'item.{iid}.saved'] != 'True':
            raise Fail('the guest\'s build is not in the host\'s save slots')

        # the town's people. The two reports are written up to a second apart, so a walking person may be
        # ~1.5 m further on one of them: people standing still are compared tightly, walking ones loosely.
        samples = []
        for _ in range(3):
            time.sleep(1.1)
            samples.append((report(hp), report(gp)))
        still, moving, worst_still, worst_moving = 0, 0, 0.0, 0.0
        for i in range(6):
            k = f'civ.{i}'
            if not all(h.get(k + '.active') == 'True' and g.get(k + '.active') == 'True' for h, g in samples):
                continue
            hpos = [vec(h[k + '.pos']) for h, _ in samples]
            d = dist(hpos[-1], vec(samples[-1][1][k + '.pos']))
            if max(dist(a, hpos[0]) for a in hpos) < 0.2:
                still += 1
                worst_still = max(worst_still, d)
            else:
                moving += 1
                worst_moving = max(worst_moving, d)
        print(f'  people: {still} standing (worst {worst_still:.2f} m), {moving} walking (worst {worst_moving:.2f} m)')
        if still + moving == 0 or worst_still > 0.3 or worst_moving > 3.0:
            raise Fail(f'people differ: standing worst {worst_still:.2f} m, walking worst {worst_moving:.2f} m')

        # the host goes to bed: the day rolls over for both, at the host's time
        d0 = int(report(hp)['game.day'])
        command(hp, 'sleep')
        h = wait(hp, lambda r: int(r['game.day']) == d0 + 1, 15, 'host slept: next day')
        g = wait(gp, lambda r: int(r['game.day']) == d0 + 1, 15, 'guest is in the next day too')
        h, g = report(hp), report(gp)
        dh = abs(float(h['time.hourf']) - float(g['time.hourf']))
        print(f'  after sleeping: host {h["time.hourf"]} h, guest {g["time.hourf"]} h, day {g["game.day"]}')
        if min(dh, 24 - dh) > 0.1:
            raise Fail('clocks differ after the night by %.2f h' % dh)

        # the co-op panel on both screens (the games' own captures)
        command(hp, 'panel open')
        command(gp, 'panel open')
        time.sleep(1.5)
        command(hp, 'screenshot pair-host-panel.png')
        command(gp, 'screenshot pair-guest-panel.png')
        time.sleep(2)

        # the guest quits: the host forgets it
        guest.terminate()
        guest.wait(20)
        wait(hp, lambda r: r['coop.players'] == '1' and r['puppets'] == '0', 20, 'host dropped the guest')
        if rt.sha(os.path.join(gp, rt.OWN_SAVE)) != seed_hash:
            raise Fail('the guest\'s own save changed')
        return 'RESULT: PASS'
    finally:
        if not keep:
            for p in procs:
                if p.poll() is None:
                    p.terminate()
                    try:
                        p.wait(15)
                    except subprocess.TimeoutExpired:
                        p.kill()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--no-build', action='store_true')
    ap.add_argument('--keep', action='store_true')
    a = ap.parse_args()
    if not a.no_build:
        for cmd in (['build_plugins.py'], ['coop/make_test_slot.py', '--slot', '1'], ['coop/make_test_slot.py', '--slot', '2']):
            p = subprocess.run([sys.executable, os.path.join(rt.ROOT, 'tools', *cmd[0].split('/')), *cmd[1:]], capture_output=True, text=True)
            if p.returncode != 0:
                print((p.stdout + p.stderr)[-3000:])
                print(f'RESULT: FAIL {cmd[0]} failed')
                sys.exit(1)
    try:
        result = run(a.keep)
    except Fail as e:
        result = f'RESULT: FAIL {e}'
    errors = rt.coop_errors(os.path.join(PROFILES, 'pair-host')) + rt.coop_errors(os.path.join(PROFILES, 'pair-guest'))
    if errors and 'PASS' in result:
        result = f'RESULT: FAIL {len(errors)} co-op error(s) in the logs, first: {errors[0]}'
    print(result)
    sys.exit(0 if 'PASS' in result else 1)


if __name__ == '__main__':
    main()
