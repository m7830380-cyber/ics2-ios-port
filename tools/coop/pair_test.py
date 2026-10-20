"""Two real game copies in co-op: a host and a guest on one PC, no bot in between.

  python tools/coop/pair_test.py [--no-build] [--keep]

The games are driven through their test file channel (-ics2test: report.txt written every second,
commands.txt executed). Checks, in order: the guest joins and matches the host's world; each sees the other's
body where the other is; money changes on either side reach both; clocks agree; an item picked up and built
by one is carried and placed on the other (both directions); the town's people stand in the same places and
play the same animations; the other people and animals (robber, police, beggar, staff, cats, dogs ...) are
the same and in the same places; lights, power, doors, progress keys, dirt, bills, rooms bought, the elevator
and the weather agree; someone at the PC is seen sitting; a guest's punch lands on the host's customer; the
panel draws; when the guest quits, the host drops its body; the guest's own save never changed.
Prints one RESULT line. Needs test slots 1 and 2 (make_test_slot.py) and the snapshot fixture (run_test.py
snapshot) for the guest's own save.
"""
import argparse, hashlib, math, os, shutil, subprocess, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import run_test as rt  # noqa: E402

PROFILES = os.path.join(rt.TEST_ROOT, 'profiles')
ADDRESS = '127.0.0.1'


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
            '-ics2profile', profile, '-ics2test', '-iosgraphics', '-ics2name', name, '-logFile', os.path.join(profile, 'player.log')] + extra
    return subprocess.Popen(args)


def free_mb():
    try:
        out = subprocess.run(['wmic', 'OS', 'get', 'FreePhysicalMemory'], capture_output=True, text=True).stdout
        return int(out.split()[1]) // 1024
    except Exception:
        return -1


def money(r):
    return float(r['game.money'])


def same(hp, gp, key, timeout, what):
    """Waits until host and guest report the same value for key."""
    deadline = time.time() + timeout
    h = g = {}
    while time.time() < deadline:
        h, g = report(hp), report(gp)
        if key in h and key in g and h[key] == g[key]:
            print(f'  ok: {what} ({key}={h[key]})', flush=True)
            return h[key]
        time.sleep(0.5)
    raise Fail(f'{what}: host {key}={h.get(key)} guest {key}={g.get(key)}')


def actors(r):
    out = {}
    for k, v in r.items():
        if k.startswith('actor.') and k.count('.') == 2:
            _, i, f = k.split('.')
            out.setdefault(int(i), {})[f] = v
    return out


def world_checks(hp, gp):
    # the other people and animals: same ones out, in the same places, the guest's AI off
    time.sleep(2)
    h, g = report(hp), report(gp)
    if h.get('actors.count') in (None, '-1') or h.get('actors.count') != g.get('actors.count'):
        raise Fail(f'actors: host {h.get("actors.count")} guest {g.get("actors.count")}')
    ha, ga = actors(h), actors(g)
    worst, n_active, ai_on = 0.0, 0, []
    for i, a in ha.items():
        b = ga.get(i, {})
        if a.get('active') != b.get('active'):
            raise Fail(f'actor {i} ({a.get("name")}): active host {a.get("active")} guest {b.get("active")}')
        if a.get('active') == 'True':
            n_active += 1
            worst = max(worst, dist(vec(a['pos']), vec(b['pos'])))
        if b.get('ai') == 'True':
            ai_on.append(a.get('name'))
    print(f'  actors: {len(ha)} shared, {n_active} out now, worst {worst:.2f} m apart')
    if ai_on:
        raise Fail('guest still runs the AI of ' + ', '.join(ai_on))
    if worst > 3.0:
        raise Fail(f'an actor is {worst:.2f} m off on the guest')
    if h.get('world.animals') != g.get('world.animals'):
        raise Fail(f'animals: host {h.get("world.animals")} guest {g.get("world.animals")}')
    print(f'  ok: same cats and dogs out ({h.get("world.animals")})')

    # customers: the same animation state (allowing for one in a transition)
    detail(hp, 'civs=' + ','.join(str(i) for i in range(54)))
    detail(gp, 'civs=' + ','.join(str(i) for i in range(54)))
    time.sleep(1.5)
    match = total = 0
    for _ in range(3):
        h, g = report(hp), report(gp)
        for i in range(54):
            k = f'civ.{i}'
            if h.get(k + '.active') == 'True' and g.get(k + '.active') == 'True':
                total += 1
                match += h.get(k + '.state') == g.get(k + '.state')
        time.sleep(0.7)
    print(f'  customer animations: {match}/{total} samples in the same state')
    if total == 0 or match < total * 0.8:
        raise Fail(f'customers animate differently ({match}/{total})')

    # switches, both ways
    l0 = report(hp)['world.lights']
    command(gp, 'lights')
    wait(hp, lambda r: r['world.lights'] != l0, 10, 'guest switched the lights: host')
    command(hp, 'lights')
    wait(gp, lambda r: r['world.lights'] == l0, 10, 'host switched them back: guest')
    p0 = report(hp)['world.power']
    command(hp, 'power')
    wait(gp, lambda r: r['world.power'] != p0, 10, 'host pulled the power switch: guest')
    command(gp, 'power')
    wait(hp, lambda r: r['world.power'] == p0, 10, 'guest switched power back on: host')
    r0 = report(hp)
    d0, pick = r0['world.doors'], r0.get('world.doorpick', '-1')
    if pick == '-1':
        raise Fail('no door that opens')
    if not d0:
        raise Fail('no doors reported')
    command(hp, f'door {pick}')
    wait(hp, lambda r: r['world.doors'] != d0, 10, 'host opened / closed door 0')
    same(hp, gp, 'world.doors', 10, 'doors agree')
    command(gp, f'door {pick}')
    wait(hp, lambda r: r['world.doors'] == d0, 10, 'guest put door 0 back: host')
    same(hp, gp, 'world.doors', 10, 'doors agree again')

    # progress keys, both ways
    command(gp, 'pref from-guest')
    wait(hp, lambda r: r['world.testpref'] == 'from-guest', 10, 'guest progress key reached the host')
    command(hp, 'pref from-host')
    wait(gp, lambda r: r['world.testpref'] == 'from-host', 10, 'host progress key reached the guest')

    # dirt: the host's dice, the guest's mop
    n0 = int(report(hp)['world.dirt'])
    command(hp, 'dirt 3')
    wait(hp, lambda r: int(r['world.dirt']) > n0, 10, 'dirt appeared on the host')
    dn = same(hp, gp, 'world.dirt', 10, 'same dirt on both')
    command(gp, 'mop')
    wait(hp, lambda r: int(r['world.dirt']) == int(dn) - 1, 10, 'guest mopped: gone on the host')
    same(hp, gp, 'world.dirt', 10, 'same dirt after mopping')

    # bills: a new one on the host is on the guest; the guest pays it once
    b0 = int(report(hp)['world.bills'])
    command(hp, 'bill 40')
    wait(gp, lambda r: int(r['world.bills']) == b0 + 1, 10, 'new bill shown on the guest')
    m0 = money(report(hp))
    command(gp, 'paybill')
    wait(hp, lambda r: int(r['world.bills']) == b0 and abs(money(r) - (m0 - 40)) < 0.01, 10, 'guest paid it: gone on the host, money -40 once')
    time.sleep(1.5)
    if abs(money(report(hp)) - (m0 - 40)) > 0.01:
        raise Fail('the bill was paid twice')

    # a room bought by the host (money given first)
    command(hp, 'money 100000')
    time.sleep(1)
    u0 = report(hp)['world.upgrades']
    m0 = money(report(hp))
    command(hp, 'upgrade')
    h = wait(hp, lambda r: r['world.upgrades'] != u0, 10, 'host bought a room')
    same(hp, gp, 'world.upgrades', 10, 'the guest has it too')
    time.sleep(1.5)
    spent = m0 - money(report(hp))
    gm_ = money(report(gp))
    print(f'  room cost {spent:.0f}; host money {money(report(hp)):.0f}, guest {gm_:.0f}')
    if abs(money(report(hp)) - gm_) > 0.01:
        raise Fail('money differs after buying a room')

    # the elevator: list its buttons, ride with every "go to" button until the car moves; both cars agree
    command(hp, 'elevatorbuttons')
    time.sleep(1)
    h = report(hp)
    if 'elevator.0.y' in h:
        y0 = float(h['elevator.0.y'])
        moved = False
        for b in range(8):
            command(hp, f'elevator {b}')
            time.sleep(4)
            if abs(float(report(hp)['elevator.0.y']) - y0) > 0.5:
                moved = True
                break
        if not moved:
            raise Fail('the elevator did not move on the host')
        time.sleep(20)
        h, g = report(hp), report(gp)
        dy = abs(float(h['elevator.0.y']) - float(g['elevator.0.y']))
        print(f'  elevator: host car at {h["elevator.0.y"]}, guest car at {g["elevator.0.y"]}')
        if dy > 0.05:
            raise Fail(f'elevator cars differ by {dy:.2f} m')
    else:
        print('  (no elevator in this scene)')

    # weather
    same(hp, gp, 'world.weather', 15, 'same weather')

    # someone at the PC is seen sitting there (their own camera zooms; the other's does not)
    command(gp, 'pc enter')
    wait(gp, lambda r: r['player.atpc'] == 'True', 10, 'guest sat down at the PC')
    wait(hp, lambda r: r.get('puppet.1.pose') == '1' and r.get('puppet.1.sitting') == 'True', 10, 'host sees the guest sitting')
    if report(hp)['player.atpc'] != 'False':
        raise Fail('the host\'s own camera went to the PC too')
    command(hp, 'shotpuppet pair-guest-at-pc.png')
    time.sleep(3)
    command(gp, 'pc exit')
    wait(hp, lambda r: r.get('puppet.1.pose') == '0', 10, 'guest got up: host sees it standing')

    # a guest's punch lands on the host's customer
    c = None
    h = report(hp)
    for i in range(54):
        if h.get(f'civ.{i}.active') == 'True' and h.get(f'civ.{i}.enum') not in ('Dead', None):
            c = i
            break
    if c is not None:
        r0 = int(report(hp)['calls.replayed']) if 'calls.replayed' in h else 0
        command(gp, f'punchciv {c}')
        wait(hp, lambda r: 'guest punched' in r.get('calls.recent', ''), 10, f'guest punched customer {c}: the host ran the hit')
    errs = int(report(hp)['calls.failed']) + int(report(gp)['calls.failed'])
    if errs:
        raise Fail(f'{errs} mirrored call(s) failed (see the logs)')


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
        guest = launch(2, gp, 'Guest', ['-ics2coop', 'join', ADDRESS])
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

        world_checks(hp, gp)

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
    ap.add_argument('--address', default='127.0.0.1', help='address the guest joins (e.g. the Radmin VPN 26.x address of this PC)')
    a = ap.parse_args()
    global ADDRESS
    ADDRESS = a.address
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
