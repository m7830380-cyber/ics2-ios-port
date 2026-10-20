"""Run co-op scenarios against a real (Windows) game copy and print one RESULT line per scenario.

  python tools/coop/run_test.py [scenario ...] [--slot 1] [--keep]

For each scenario: start the test slot's game (windowed, muted, own profile folder, -ics2test, auto-start,
hosting), run the headless bot (coop/bot) against it, then stop the game process this script started (by
PID, never by name) and check the game's own log for exceptions from the co-op code. Exit code 0 only if
every scenario printed RESULT: PASS. `selftest` (the scorer must be able to fail) always runs first.
"""
import argparse, os, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TEST_ROOT = os.path.join(os.path.expanduser('~'), 'ics2-test')
BOT = os.path.join(ROOT, 'coop', 'bot')
BOT_DLL = os.path.join(BOT, 'bin', 'Release', 'net8.0', 'CoopBot.dll')
EXE = 'Internet Cafe Simulator 2.exe'
LOG = 'InternetCafeSim2-iOS.log'
# scenario -> (game role, game extra args, profile seed). Seeds are saves copied into the profile first.
SCENARIOS = {
    'handshake': ('host', [], None),
}


def run_bot(*args, timeout=600):
    p = subprocess.run(['dotnet', BOT_DLL, *args], capture_output=True, text=True, timeout=timeout)
    out = p.stdout + p.stderr
    result = [l for l in out.splitlines() if l.startswith('RESULT:')]
    return out, (result[-1] if result else 'RESULT: FAIL bot printed no result')


def coop_errors(log_path):
    """Exceptions in our log that mention co-op code. Pre-existing game errors are not counted."""
    if not os.path.exists(log_path):
        return ['game log missing']
    bad = []
    lines = open(log_path, encoding='utf-8', errors='replace').read().splitlines()
    for i, l in enumerate(lines):
        if '[E]' in l and ('coop' in l.lower() or 'Coop' in ''.join(lines[i:i + 3])):
            bad.append(l.strip())
    return bad


def run_scenario(name, slot, keep):
    role, extra, seed = SCENARIOS[name]
    profile = os.path.join(TEST_ROOT, 'profiles', f'{name}-slot{slot}')
    if os.path.isdir(profile):
        shutil.rmtree(profile)
    os.makedirs(profile)
    if seed:
        shutil.copytree(seed, profile, dirs_exist_ok=True)
    exe = os.path.join(TEST_ROOT, f'slot{slot}', EXE)
    args = [exe, '-screen-fullscreen', '0', '-screen-width', '640', '-screen-height', '360',
            '-ics2profile', profile, '-ics2test', '-ics2autostart', '-ics2name', f'Game{slot}',
            '-logFile', os.path.join(profile, 'player.log')]
    if role == 'host':
        args += ['-ics2coop', 'host']
    args += extra
    game = subprocess.Popen(args)
    print(f'--- {name}: game pid {game.pid}, profile {profile}', flush=True)
    try:
        out, result = run_bot(name, '--timeout', '240')
        print(out.rstrip(), flush=True)
        if game.poll() is not None and 'PASS' in result:
            result = f'RESULT: FAIL game exited early with code {game.returncode}'
    finally:
        if not keep:
            game.terminate()
            try:
                game.wait(15)
            except subprocess.TimeoutExpired:
                game.kill()
                game.wait(15)
    errors = coop_errors(os.path.join(profile, LOG))
    if errors and 'PASS' in result:
        result = f'RESULT: FAIL {len(errors)} co-op error(s) in the game log, first: {errors[0]}'
    print(f'{name}: {result}', flush=True)
    return 'PASS' in result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('scenarios', nargs='*', default=list(SCENARIOS))
    ap.add_argument('--slot', type=int, default=1)
    ap.add_argument('--keep', action='store_true', help='leave the game running after the scenario')
    a = ap.parse_args()
    subprocess.check_call(['dotnet', 'build', BOT, '-c', 'Release', '-nologo', '-v', 'q'], stdout=subprocess.DEVNULL)
    out, result = run_bot('selftest', timeout=60)
    print('selftest:', result)
    passed = 'PASS' in result
    for s in a.scenarios:
        passed &= run_scenario(s, a.slot, a.keep)
    print('SUITE:', 'PASS' if passed else 'FAIL')
    sys.exit(0 if passed else 1)


if __name__ == '__main__':
    main()
