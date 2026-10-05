"""Tests only isolated, self-created signed files against the actual native DLL."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

DLL, SIGNER = map(lambda p: str(Path(p).resolve()), sys.argv[1:3])
C_SAMPLE = str(Path(sys.argv[3]).resolve()) if len(sys.argv) > 3 else None
sys.argv = sys.argv[:1]
binding = Path(__file__).resolve().parents[1] / 'bindings/python/integrity_guard.py'
spec = importlib.util.spec_from_file_location('integrity_guard', binding)
ig = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ig)


class NativeConsumer(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='IntegrityGuard-Python-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.payload = b'return "verified"\n\x00\xff'
        (self.root / 'protected.lua').write_bytes(self.payload)
        subprocess.run(['dotnet', SIGNER, 'keygen', str(self.root / 'keys')], check=True, capture_output=True)
        self.key = (self.root / 'keys/public.pem').read_text('ascii')
        subprocess.run(['dotnet', SIGNER, 'sign', str(self.root / 'keys/private.pem'),
                        str(self.root), '1', 'protected.lua'], check=True, capture_output=True)

    def session(self, floor=1):
        return ig.Session(DLL, root=self.root, public_key_pem=self.key,
                          required_files=['protected.lua'], minimum_release=floor)

    def test_binary_roundtrip(self):
        with self.session() as session:
            session.check(0)
            self.assertEqual(session.read('protected.lua'), self.payload)

    def test_latch(self):
        with self.session() as session:
            session.check(0)
            (self.root / 'protected.lua').write_bytes(b'changed')
            with self.assertRaises(ig.IntegrityError) as error:
                session.read('protected.lua')
            self.assertEqual(error.exception.code, 105)
            (self.root / 'protected.lua').write_bytes(self.payload)
            with self.assertRaises(ig.IntegrityError) as error:
                session.read('protected.lua')
            self.assertEqual(error.exception.code, 108)

    def test_release_floor(self):
        with self.session(2) as session:
            with self.assertRaises(ig.IntegrityError) as error:
                session.check(0)
            self.assertEqual(error.exception.code, 104)

    def test_closed_handle(self):
        session = self.session()
        session.close()
        session.close()
        with self.assertRaises(ig.IntegrityError) as error:
            session.check()
        self.assertEqual(error.exception.code, 2)

    @unittest.skipUnless(C_SAMPLE, 'C sample executable not supplied')
    def test_c_consumer(self):
        config = self.root / 'config.json'
        config.write_text(json.dumps(dict(root=str(self.root), publicKeyPem=self.key,
                                         requiredFiles=['protected.lua'], minimumRelease=1)), encoding='utf-8')
        result = subprocess.run([C_SAMPLE, str(config), 'protected.lua'], capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        (self.root / 'protected.lua').write_bytes(b'changed')
        self.assertEqual(subprocess.run([C_SAMPLE, str(config), 'protected.lua'], capture_output=True).returncode, 1)


unittest.main(verbosity=2)
