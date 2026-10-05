"""Windows x64 ABI v1 binding. Only the Python standard library is required."""
import ctypes as C
import json
import os
from pathlib import Path

_libraries = {}  # Keep NativeAOT libraries loaded for process lifetime.


class IntegrityError(RuntimeError):
    def __init__(self, code):
        self.code = code
        super().__init__(f"IntegrityGuard status {code}")


class Session:
    def __init__(self, dll_path, *, root, public_key_pem, required_files, minimum_release):
        if os.name != "nt" or C.sizeof(C.c_void_p) != 8:
            raise RuntimeError("This package targets Windows x64")
        path = Path(dll_path)
        if not path.is_absolute():
            raise ValueError("DLL path must be absolute")
        path = str(path.resolve(strict=True))
        if path not in _libraries:
            lib = C.CDLL(path)
            lib.ig_abi_version.argtypes = []
            lib.ig_abi_version.restype = C.c_uint32
            lib.ig_open.argtypes = [C.c_char_p, C.c_uint32, C.POINTER(C.c_uint64)]
            lib.ig_open.restype = C.c_int32
            lib.ig_check.argtypes = [C.c_uint64, C.c_int32]
            lib.ig_check.restype = C.c_int32
            lib.ig_copy_file.argtypes = [C.c_uint64, C.c_char_p, C.c_uint32,
                                         C.c_void_p, C.c_uint32, C.POINTER(C.c_uint32)]
            lib.ig_copy_file.restype = C.c_int32
            lib.ig_close.argtypes = [C.c_uint64]
            lib.ig_close.restype = C.c_int32
            _libraries[path] = lib
        self._lib = _libraries[path]
        self._handle = C.c_uint64()
        if self._lib.ig_abi_version() != 1:
            raise RuntimeError("Unsupported IntegrityGuard ABI")
        config = json.dumps(dict(root=str(root), publicKeyPem=public_key_pem,
                                 requiredFiles=required_files, minimumRelease=minimum_release)).encode("utf-8")
        if len(config) > 131072:
            raise ValueError("Config exceeds 128 KiB")
        code = self._lib.ig_open(config, len(config), C.byref(self._handle))
        if code:
            raise IntegrityError(code)

    def check(self, checkpoint=2):
        if type(checkpoint) is not int or checkpoint not in (0, 1, 2):
            raise ValueError("Invalid checkpoint")
        code = self._lib.ig_check(self._handle, checkpoint)
        if code:
            raise IntegrityError(code)

    def read(self, name, checkpoint=2):
        """Recheck then copy authenticated bytes; caller must serialize session use."""
        name = name.encode("utf-8")
        if not 0 < len(name) <= 128:
            raise ValueError("Invalid filename")
        self.check(checkpoint)
        size = C.c_uint32()
        code = self._lib.ig_copy_file(self._handle, name, len(name), None, 0, C.byref(size))
        if code not in (0, 3):
            raise IntegrityError(code)
        if size.value > 33554432:
            raise IntegrityError(7)
        data = (C.c_ubyte * max(size.value, 1))()
        code = self._lib.ig_copy_file(self._handle, name, len(name), data, size.value, C.byref(size))
        if code:
            raise IntegrityError(code)
        return bytes(data[:size.value])

    def close(self):
        if self._handle.value:
            code = self._lib.ig_close(self._handle)
            self._handle.value = 0
            if code:
                raise IntegrityError(code)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
