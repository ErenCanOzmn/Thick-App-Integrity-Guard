"""Launches real app executables. Changes only unique test copies under artifacts."""
import json
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / 'artifacts' / 'application-tests' / uuid.uuid4().hex
RUN.mkdir(parents=True)
SIGNER = ROOT / 'tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll'
KEY = ROOT / '.demo-keys/private.pem'
results = []


def app_copy(name, source):
    target = RUN / name
    shutil.copytree(source, target)
    return target


class App:
    def __init__(self, directory):
        self.process = subprocess.Popen([str(directory / 'QuoteConsole.exe')],
                                        cwd=directory, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding='utf-8',
                                        creationflags=subprocess.CREATE_NO_WINDOW)
        self.lines = queue.Queue()
        def pump():
            for line in self.process.stdout:
                self.lines.put(line)
            self.lines.put(None)
        threading.Thread(target=pump, daemon=True).start()

    def receive(self):
        line = self.lines.get(timeout=15)
        if line is None:
            raise AssertionError('Application closed before response: ' + self.process.stderr.read())
        return json.loads(line)

    def command(self, text):
        self.process.stdin.write(text + '\n')
        self.process.stdin.flush()
        return self.receive()

    def close(self):
        if self.process.poll() is None:
            try:
                self.process.stdin.write('exit\n')
                self.process.stdin.flush()
                self.process.wait(timeout=10)
            except (BrokenPipeError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait(timeout=5)
        self.process.stdin.close()
        self.process.stdout.close()
        self.process.stderr.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def require(condition, detail):
    if not condition:
        raise AssertionError(str(detail))


def check(name, body):
    try:
        evidence = body()
        results.append(dict(name=name, passed=True, evidence=evidence))
        print('PASS', name)
    except Exception as exc:
        results.append(dict(name=name, passed=False, error=str(exc)))
        print('FAIL', name, str(exc))


def clean_quote():
    path = app_copy('console-clean', ROOT / 'artifacts/apps/QuoteConsole')
    with App(path) as app:
        initial = app.receive()
        quote = app.command('quote 2')
        invalid = app.command('quote -1')
        require(initial['allowed'] and quote['allowed'] and quote['total'] == 251, quote)
        require(not invalid['allowed'] and invalid['code'] == 'InvalidCommand', invalid)
        return [initial, quote, invalid]


def live_tamper():
    path = app_copy('console-live', ROOT / 'artifacts/apps/QuoteConsole')
    rules = path / 'rules.json'
    original = rules.read_bytes()
    with App(path) as app:
        require(app.receive()['allowed'], 'initial check failed')
        first = app.command('quote 1')
        rules.write_text('{"Product":"Changed","UnitPrice":999}', encoding='utf-8')
        rejected = app.command('quote 1')
        rules.write_bytes(original)
        latched = app.command('quote 1')
        require(first['allowed'] and not rejected['allowed'] and rejected['code'] == 'FileMismatch', rejected)
        require(not latched['allowed'] and latched['code'] == 'SessionBlocked', latched)
    with App(path) as app:
        restarted = app.receive()
        recovered = app.command('quote 1')
        require(restarted['allowed'] and recovered['allowed'], recovered)
    return [first, rejected, latched, restarted, recovered]


def startup_failure(kind):
    path = app_copy('console-' + kind, ROOT / 'artifacts/apps/QuoteConsole')
    if kind == 'missing':
        (path / 'rules.json').unlink()
        expected = 'VerificationError'
    elif kind == 'signature':
        sig = bytearray((path / 'integrity.sig').read_bytes())
        sig[0] ^= 1
        (path / 'integrity.sig').write_bytes(sig)
        expected = 'InvalidSignature'
    else:
        (path / 'rules.json').write_bytes(b'changed')
        expected = 'FileMismatch'
    with App(path) as app:
        response = app.receive()
        exit_code = app.process.wait(timeout=10)
        require(not response['allowed'] and response['code'] == expected and exit_code == 1,
                dict(response=response, exit_code=exit_code))
        return dict(response=response, exit_code=exit_code)


def invalid_business_schema():
    path = app_copy('console-invalid-schema', ROOT / 'artifacts/apps/QuoteConsole')
    (path / 'rules.json').write_text('{"Product":"Missing price"}', encoding='utf-8')
    subprocess.run(['dotnet', str(SIGNER), 'sign', str(KEY), str(path), '1', 'rules.json'],
                   check=True, capture_output=True)
    with App(path) as app:
        startup = app.receive()
        rejected = app.command('quote 1')
        require(startup['allowed'] and not rejected['allowed'] and rejected['code'] == 'InvalidBusinessRules', rejected)
        return [startup, rejected]


def winforms_verify():
    path = app_copy('winforms-cli', ROOT / 'artifacts/demo')
    executable = str(path / 'DemoApp.exe')
    clean = subprocess.run([executable, '--verify'], timeout=15).returncode
    (path / 'demo-rules.json').write_text('{"Product":"Changed","UnitPrice":999}', encoding='utf-8')
    changed = subprocess.run([executable, '--verify'], timeout=15).returncode
    require(clean == 0 and changed == 1, dict(clean=clean, changed=changed))
    return dict(clean_exit=clean, changed_exit=changed)


check('console clean calculation and invalid quantity', clean_quote)
check('console live tamper, repair latch, restart recovery', live_tamper)
for kind in ('changed', 'missing', 'signature'):
    check('console startup ' + kind, lambda kind=kind: startup_failure(kind))
check('console signed invalid business schema', invalid_business_schema)
check('WinForms executable verify mode', winforms_verify)
report = dict(run_directory=str(RUN), results=results,
              native='Not exercised by this suite; run build-native.ps1 separately',
              lua='Not exercised by this suite; run the Lua consumer tests separately')
(RUN / 'process-report.json').write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
print('REPORT', RUN / 'process-report.json')
print(f'{sum(item["passed"] for item in results)}/{len(results)} application process scenarios passed')
raise SystemExit(0 if all(item['passed'] for item in results) else 1)
