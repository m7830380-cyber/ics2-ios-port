"""Run co-op scenarios against a real (Windows) game copy and print one RESULT line per scenario.

  python tools/coop/run_test.py [scenario ...] [--slot 1] [--keep]

For each scenario: start the test slot's game (windowed, muted, own profile folder, -ics2test, auto-start,
hosting), run the headless bot (coop/bot) against it, then stop the game process this script started (by
PID, never by name) and check the game's own log for exceptions from the co-op code. Exit code 0 only if
every scenario printed RESULT: PASS. `selftest` (the scorer must be able to fail) always runs first.
"""
import argparse, hashlib, os, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TEST_ROOT = os.path.join(os.path.expanduser('~'), 'ics2-test')
BOT = os.path.join(ROOT, 'coop', 'bot')
BOT_DLL = os.path.join(BOT, 'bin', 'Release', 'net8.0', 'CoopBot.dll')
EXE = 'Internet Cafe Simulator 2.exe'
LOG = 'InternetCafeSim2-iOS.log'
FIXTURES = os.path.join(TEST_ROOT, 'fixtures')  # game data captured by tests: local only, never committed
OWN_SAVE = 'save-data.save'
# scenario -> (bot scenario, game role, game extra args, profile seed, bot args, must fail).
# Seeds are saves copied into the profile first. "Mutant" scenarios check that a test can fail: the bot is
# told to expect something wrong, and the suite passes only if that run FAILS.
SCENARIOS = {
    'handshake': ('handshake', 'host', [], None, [], False),
    'puppets': ('puppets', 'host', [], None, [], False),
    'puppets-mutant': ('puppets', 'host', [], None, ['--mutate'], True),
    'snapshot': ('snapshot', 'host', [], None, ['--fixture', FIXTURES], False),
    # guest: the bot hosts; the game has its own save (seeded) and joins from the main menu
    'join': ('join', 'guest', [], 'own-save', ['--fixture', FIXTURES], False),
    'join-mutant': ('join', 'guest', [], 'own-save', ['--fixture', FIXTURES, '--mutate'], True),
    'economy-host': ('economy-host', 'host', [], None, [], False),
    'economy-guest': ('economy-guest', 'guest', [], 'own-save', ['--fixture', FIXTURES], False),
    'items-host': ('items-host', 'host', [], None, [], False),
    'items-host-mutant': ('items-host', 'host', [], None, ['--mutate'], True),
    'items-guest': ('items-guest', 'guest', [], 'own-save', ['--fixture', FIXTURES], False),
    'economy-guest-mutant': ('economy-guest', 'guest', [], 'own-save', ['--fixture', FIXTURES, '--mutate'], True),
}


def sha(path):
    return hashlib.sha256(open(path, 'rb').read()).hexdigest() if os.path.exists(path) else None


def guest_after_session(profile, seed_hash):
    """After the bot (host) left: the guest went back to its own save and the main menu, and its own save
    file is byte for byte what it was before the session."""
    log = os.path.join(profile, LOG)
    deadline = time.time() + 30
    while time.time() < deadline:
        text = open(log, encoding='utf-8', errors='replace').read() if os.path.exists(log) else ''
        tail = text[text.rfind('back to the own save file'):] if 'back to the own save file' in text else ''
        if "scene loaded: 'MainMenu'" in tail:
            break
        time.sleep(1)
    else:
        return 'RESULT: FAIL guest did not return to its own save and the main menu after the host left'
    if sha(os.path.join(profile, OWN_SAVE)) != seed_hash:
        return 'RESULT: FAIL the guest own save file changed during the session'
    return None


def run_bot(*args, timeout=600):
    p = subprocess.run(['dotnet', BOT_DLL, *args], capture_output=True, text=True, timeout=timeout)
    out = p.stdout + p.stderr
    result = [l for l in out.splitlines() if l.startswith('RESULT:')]
    return out, (result[-1] if result else 'RESULT: FAIL bot printed no result')


COOP_FRAMES = ('ICS2iOS.Coop', 'ICS2iOS.PlayerSync', 'ICS2Coop.')


def coop_errors(profile):
    """Errors caused by co-op code: our log's "coop:" error lines, plus entries in Unity's player log whose
    stack trace runs through co-op classes. The game's own pre-existing errors are not counted."""
    log_path, player_log = os.path.join(profile, LOG), os.path.join(profile, 'player.log')
    if not os.path.exists(log_path) or not os.path.exists(player_log):
        return ['game log missing']
    bad = [l.strip() for l in open(log_path, encoding='utf-8', errors='replace')
           if '[E]' in l and 'coop:' in l]
    text = open(player_log, encoding='utf-8', errors='replace').read().replace('\r\n', '\n')
    for block in text.split('\n\n'):
        if any(f in block for f in COOP_FRAMES) and ('Exception' in block or 'Error' in block or "Can't" in block):
            if '[ICS2-iOS]' in block.splitlines()[0]:
                continue  # our own info lines carry a stack trace too
            bad.append(block.strip().splitlines()[0])
    return bad


def scanner_selftest():
    """The log scanner must flag a co-op error and ignore a game error and our own info lines."""
    d = os.path.join(TEST_ROOT, 'profiles', '_scanner-selftest')
    os.makedirs(d, exist_ok=True)
    open(os.path.join(d, LOG), 'w').write('10:00:00.000 [I] [ICS2-iOS] coop: hosting\n')
    open(os.path.join(d, 'player.log'), 'w').write(
        "Shader error in 'Blur x5'\nUnityEngine.Material:SetTexture\nPauseMenu:Start ()\n\n"
        "[ICS2-iOS] coop: created puppet\nICS2iOS.PlayerSync:GetPuppet (int)\n\n"
        "Can't remove Rigidbody because CharacterJoint depends on it\nUnityEngine.Object:DestroyImmediate\n"
        "ICS2iOS.PlayerSync:MakeBody (int)\n\n")
    found = coop_errors(d)
    ok = len(found) == 1 and "Can't remove" in found[0]
    print('scanner selftest:', 'PASS' if ok else f'FAIL {found}')
    return ok


def run_scenario(name, slot, keep):
    bot_scenario, role, extra, seed, bot_args, must_fail = SCENARIOS[name]
    profile = os.path.join(TEST_ROOT, 'profiles', f'{name}-slot{slot}')
    if os.path.isdir(profile):
        shutil.rmtree(profile)
    os.makedirs(profile)
    seed_hash = None
    if seed == 'own-save':
        src = os.path.join(FIXTURES, 'own-save.save')
        if not os.path.exists(src):
            print(f'{name}: RESULT: FAIL no fixture; run the snapshot scenario first')
            return False
        shutil.copyfile(src, os.path.join(profile, OWN_SAVE))
        seed_hash = sha(src)
    exe = os.path.join(TEST_ROOT, f'slot{slot}', EXE)
    args = [exe, '-screen-fullscreen', '0', '-screen-width', '640', '-screen-height', '360',
            '-ics2profile', profile, '-ics2test', '-ics2name', f'Game{slot}',
            '-logFile', os.path.join(profile, 'player.log')]
    if role == 'host':
        args += ['-ics2autostart', '-ics2coop', 'host']
    elif role == 'guest':
        args += ['-ics2coop', 'join', '127.0.0.1']
    args += extra
    game = subprocess.Popen(args)
    print(f'--- {name}: game pid {game.pid}, profile {profile}', flush=True)
    try:
        out, result = run_bot(bot_scenario, '--timeout', '240', *bot_args)
        print(out.rstrip(), flush=True)
        if game.poll() is not None and 'PASS' in result:
            result = f'RESULT: FAIL game exited early with code {game.returncode}'
        if role == 'guest' and 'PASS' in result:
            result = guest_after_session(profile, seed_hash) or result
    finally:
        if not keep:
            game.terminate()
            try:
                game.wait(15)
            except subprocess.TimeoutExpired:
                game.kill()
                game.wait(15)
    errors = coop_errors(profile)
    if errors and 'PASS' in result:
        result = f'RESULT: FAIL {len(errors)} co-op error(s) in the game log, first: {errors[0]}'
    if must_fail:
        ok = 'FAIL' in result and 'exception' not in result
        print(f'{name}: {result} -> mutant {"caught (good)" if ok else "NOT caught"}', flush=True)
        return ok
    print(f'{name}: {result}', flush=True)
    return 'PASS' in result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('scenarios', nargs='*', default=list(SCENARIOS))
    ap.add_argument('--slot', type=int, default=1)
    ap.add_argument('--keep', action='store_true', help='leave the game running after the scenario')
    ap.add_argument('--no-build', action='store_true', help='test the current slot without rebuilding')
    a = ap.parse_args()
    if not a.no_build:
        # never test a stale build: rebuild the plugins and refresh the slot, stop on any failure
        for cmd in (['build_plugins.py'], ['coop/make_test_slot.py', '--slot', str(a.slot)]):
            p = subprocess.run([sys.executable, os.path.join(ROOT, 'tools', *cmd[0].split('/')), *cmd[1:]],
                               capture_output=True, text=True)
            if p.returncode != 0:
                print((p.stdout + p.stderr)[-3000:])
                print(f'SUITE: FAIL {cmd[0]} failed')
                sys.exit(1)
        print('build: plugins rebuilt, slot refreshed')
    subprocess.check_call(['dotnet', 'build', BOT, '-c', 'Release', '-nologo', '-v', 'q'], stdout=subprocess.DEVNULL)
    out, result = run_bot('selftest', timeout=60)
    print('selftest:', result)
    passed = 'PASS' in result and scanner_selftest()
    for s in a.scenarios:
        passed &= run_scenario(s, a.slot, a.keep)
    print('SUITE:', 'PASS' if passed else 'FAIL')
    sys.exit(0 if passed else 1)


if __name__ == '__main__':
    main()
