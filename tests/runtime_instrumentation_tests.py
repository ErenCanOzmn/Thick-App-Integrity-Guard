"""Compare this repository's synthetic DPAPI lab with/without its runtime gate.

Loads the operator-selected, unmodified SafiyeMonitor Frida script into ONLY
RuntimeProbe.exe launched here. Reports hashes/counts, never plaintext payloads.
Requires Python frida and a built samples/RuntimeProbe project.
"""
import argparse
import hashlib
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import uuid

import frida

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--safiye-script', type=Path, required=True)
parser.add_argument('--native-dll', type=Path)
args = parser.parse_args()
source = args.safiye_script.read_text(encoding='utf-8')
exe = ROOT / 'artifacts/runtime-probe/RuntimeProbe.exe'
if args.native_dll:
    assert args.native_dll.is_absolute() and args.native_dll.is_file()
    target_command = [sys.executable, str(ROOT / 'tests/native_runtime_probe.py'), str(args.native_dll)]
    identity = args.native_dll
    target_kind = 'Python ctypes host for actual NativeAOT DLL'
else:
    assert exe.is_file(), 'Build samples/RuntimeProbe to artifacts/runtime-probe first.'
    target_command = [str(exe)]
    identity = exe
    target_kind = 'managed RuntimeProbe.exe'
run = ROOT / 'artifacts/runtime-tests' / uuid.uuid4().hex
run.mkdir(parents=True)
results = []


class App:
    def __init__(self, unguarded=False):
        self.process = subprocess.Popen([*target_command, *(['--unguarded'] if unguarded else [])],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding='utf-8',
                                        creationflags=subprocess.CREATE_NO_WINDOW)
        self.lines = queue.Queue()

        def pump():
            for line in self.process.stdout:
                self.lines.put(line)
            self.lines.put(None)
        threading.Thread(target=pump, daemon=True).start()

    def receive(self):
        line = self.lines.get(timeout=20)
        if line is None:
            raise AssertionError('Lab exited early: ' + self.process.stderr.read())
        return json.loads(line)

    def command(self, command):
        self.process.stdin.write(command + '\n')
        self.process.stdin.flush()
        return self.receive()

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.write('exit\n')
            self.process.stdin.flush()
            try:
                self.process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            stream.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def instrumented(unguarded):
    captured = []
    errors = []
    lock = threading.Lock()

    def message(message, data):
        with lock:
            if message['type'] == 'error':
                errors.append(message.get('description', 'script error'))
            elif message['type'] == 'send':
                payload = message.get('payload', {})
                if isinstance(payload, dict) and payload.get('type') == 'crypto_event' and \
                        payload.get('api') == 'CryptUnprotectData':
                    captured.append(dict(api=payload['api'], bytes=len(data or b''),
                                         sha256=hashlib.sha256(data or b'').hexdigest().upper()))

    with App(unguarded) as app:
        startup = app.receive()
        assert startup['allowed'], startup
        session = frida.get_local_device().attach(app.process.pid)
        script = session.create_script(source)
        script.on('message', message)
        try:
            script.load()  # preloaded crypto DLLs: hookCrypto installs synchronously
            response = app.command('use')
            # Event delivery and stdout are separate channels. Wait only for an
            # expected positive event, not to define the guard's detection window.
            deadline = time.monotonic() + 5
            if unguarded:
                while time.monotonic() < deadline:
                    with lock:
                        if captured:
                            break
                    time.sleep(0.05)
            else:
                time.sleep(0.25)
            with lock:
                observed = list(captured)
                script_errors = list(errors)
            assert not script_errors, script_errors
            if unguarded:
                assert response['allowed'], response
                assert any(item['sha256'] == response['plaintextSha256'].upper()
                           for item in observed), (response, observed)
            else:
                assert not response['allowed'] and response['code'] == 'RuntimeNativeEntryChanged', response
                assert not observed, observed
            script.unload()
            session.detach()
            session = None
            after_detach = app.command('use')
            if not unguarded:
                assert not after_detach['allowed'] and \
                    after_detach['code'] == 'RuntimeSessionBlocked', after_detach
            return dict(startup=startup, operation=response, captures=observed,
                        after_detach=after_detach, script_errors=script_errors)
        finally:
            if session is not None:
                session.detach()


def clean_restart():
    with App() as app:
        startup = app.receive()
        use = app.command('use')
        assert startup['allowed'] and use['allowed'] and use['bytes'] > 0, (startup, use)
        return dict(startup=startup, operation=use)


def scoped_attachment(module=None, export=None):
    """Document the boundary: passive attach passes; selected entry hook fails."""
    with App() as app:
        startup = app.receive()
        assert startup['allowed'], startup
        session = frida.get_local_device().attach(app.process.pid)
        try:
            if module:
                address = 'Module.getExportByName(' + json.dumps(module) + ',' + json.dumps(export) + ')'
                source = 'Interceptor.attach(' + address + ', {onEnter: function () {}});'
            else:
                source = 'send({type: "lab_ready"});'
            script = session.create_script(source)
            errors = []
            script.on('message', lambda message, data:
                      errors.append(message['description']) if message['type'] == 'error' else None)
            script.load()
            response = app.command('use')
            assert not errors, errors
            if module:
                assert not response['allowed'] and response['code'] == 'RuntimeNativeEntryChanged', response
            else:
                assert response['allowed'] and response['bytes'] == 60, response
            return dict(startup=startup, operation=response, monitored_module=module, monitored_export=export)
        finally:
            session.detach()


for name, body in [('unguarded Safiye captures the exact synthetic DPAPI data',
                    lambda: instrumented(True)),
                   ('guarded Safiye hook blocks operation and latches after detach',
                    lambda: instrumented(False)),
                   ('fresh guarded process works without instrumentation', clean_restart),
                   ('passive Frida attachment alone is outside the gate', scoped_attachment),
                   ('BCryptFinishHash entry hook is detected',
                    lambda: scoped_attachment('bcrypt.dll', 'BCryptFinishHash')),
                   ('NCryptVerifySignature entry hook is detected',
                    lambda: scoped_attachment('ncrypt.dll', 'NCryptVerifySignature'))]:
    try:
        evidence = body()
        results.append(dict(name=name, passed=True, evidence=evidence))
        print('PASS', name)
    except Exception as exc:
        results.append(dict(name=name, passed=False, error=str(exc)))
        print('FAIL', name, str(exc))

report = dict(frida_version=frida.__version__,
              safiye_script_sha256=hashlib.sha256(args.safiye_script.read_bytes()).hexdigest(),
              target_sha256=hashlib.sha256(identity.read_bytes()).hexdigest(),
              target_kind=target_kind,
              scope='This repository synthetic lab only; post-startup attach.',
              results=results)
path = run / 'runtime-report.json'
path.write_text(json.dumps(report, indent=2), encoding='utf-8')
print('REPORT', path)
print(f'{sum(item["passed"] for item in results)}/{len(results)} runtime scenarios passed')
raise SystemExit(0 if all(item['passed'] for item in results) else 1)
