"""Exercise the real signer; rejected input must preserve an existing signed pair."""
import json
from pathlib import Path
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUN = ROOT / 'artifacts' / 'signer-tests' / uuid.uuid4().hex
RUN.mkdir(parents=True)
SIGNER = ROOT / 'tools/ManifestSigner/bin/Release/net8.0/ManifestSigner.dll'
results = []


def invoke(*args):
    return subprocess.run(['dotnet', str(SIGNER), *map(str, args)],
                          capture_output=True, text=True, timeout=30)


keys = RUN / 'keys'
assert invoke('keygen', keys).returncode == 0
output = RUN / 'output'
output.mkdir()
(output / 'rules.json').write_text('{"UnitPrice":12.50}', encoding='utf-8')
key = keys / 'private.pem'
assert invoke('sign', key, output, 1, 'rules.json').returncode == 0
original = [(output / name).read_bytes() for name in ('integrity.json', 'integrity.sig')]


def rejected(name, files, signing_key=key):
    result = invoke('sign', signing_key, output, 2, *files)
    preserved = original == [(output / item).read_bytes()
                             for item in ('integrity.json', 'integrity.sig')]
    passed = result.returncode != 0 and preserved
    results.append(dict(name=name, passed=passed, exit_code=result.returncode,
                        previous_pair_preserved=preserved))
    print(('PASS ' if passed else 'FAIL ') + name)


rejected('duplicate inventory', ['rules.json', 'RULES.JSON'])
rejected('unsafe filename', ['../rules.json'])
(output / 'with space.json').write_text('{}', encoding='utf-8')
rejected('filename unsupported by verifier', ['with space.json'])
rejected('missing input', ['missing.json'])
rejected('manifest cannot protect itself', ['integrity.json'])
rejected('signature cannot protect itself', ['integrity.sig'])
rejected('public key cannot sign', ['rules.json'], keys / 'public.pem')
many = [f'file{i}.bin' for i in range(33)]
for name in many:
    (output / name).write_bytes(b'x')
rejected('more than 32 files', many)
with (output / 'large.bin').open('wb') as stream:
    stream.truncate(32 * 1024 * 1024 + 1)
rejected('file exceeds 32 MiB', ['large.bin'])
for name, size in [('first.bin', 32 * 1024 * 1024),
                   ('second.bin', 32 * 1024 * 1024), ('last.bin', 1)]:
    with (output / name).open('wb') as stream:
        stream.truncate(size)
rejected('inventory exceeds 64 MiB', ['first.bin', 'second.bin', 'last.bin'])

# A valid new release must still replace the pair after the negative scenarios.
(output / 'rules.json').write_text('{"UnitPrice":25.00}', encoding='utf-8')
updated = invoke('sign', key, output, 2, 'rules.json')
passed = updated.returncode == 0 and json.loads((output / 'integrity.json').read_text())['Release'] == 2
results.append(dict(name='valid release after rejected inputs', passed=passed,
                    exit_code=updated.returncode))
print(('PASS ' if passed else 'FAIL ') + results[-1]['name'])
report = RUN / 'signer-report.json'
report.write_text(json.dumps(results, indent=2), encoding='utf-8')
print('REPORT', report)
print(f'{sum(item["passed"] for item in results)}/{len(results)} signer process scenarios passed')
raise SystemExit(0 if all(item['passed'] for item in results) else 1)
