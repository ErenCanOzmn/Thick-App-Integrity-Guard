"""Isolated Windows x64 JSONL laboratory host for the real NativeAOT DLL.

No Frida orchestration, network, external application, or real credential is used.
The parent test may attach Safiye's unmodified script after the startup line.
"""
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import sys


if sys.platform != 'win32' or ctypes.sizeof(ctypes.c_void_p) != 8:
    raise SystemExit('This laboratory requires Windows x64 Python.')
if len(sys.argv) not in (2, 3) or (len(sys.argv) == 3 and sys.argv[2] != '--unguarded'):
    raise SystemExit('native_runtime_probe.py <absolute native DLL> [--unguarded]')
dll_path = Path(sys.argv[1])
if not dll_path.is_absolute() or not dll_path.is_file():
    raise SystemExit('An existing absolute native DLL path is required.')
guarded = '--unguarded' not in sys.argv[2:]
# The test DLL's directory and System32 are the only dependency search locations.
guard = ctypes.CDLL(str(dll_path), winmode=0x00000100 | 0x00000800)
guard.ig_runtime_check.argtypes = [ctypes.c_int32]
guard.ig_runtime_check.restype = ctypes.c_int32
crypt32 = ctypes.WinDLL('crypt32.dll', use_last_error=True, winmode=0x00000800)
kernel32 = ctypes.WinDLL('kernel32.dll', use_last_error=True, winmode=0x00000800)
bcrypt = ctypes.WinDLL('bcrypt.dll', use_last_error=True, winmode=0x00000800)


class Blob(ctypes.Structure):
    _fields_ = [('length', wintypes.DWORD), ('data', ctypes.c_void_p)]


crypt32.CryptProtectData.argtypes = [ctypes.POINTER(Blob), ctypes.c_wchar_p,
                                    ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                    wintypes.DWORD, ctypes.POINTER(Blob)]
crypt32.CryptProtectData.restype = wintypes.BOOL
crypt32.CryptUnprotectData.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p,
                                      ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                      wintypes.DWORD, ctypes.POINTER(Blob)]
crypt32.CryptUnprotectData.restype = wintypes.BOOL
kernel32.LocalFree.argtypes = [ctypes.c_void_p]
kernel32.LocalFree.restype = ctypes.c_void_p
bcrypt.BCryptGenRandom.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD]
bcrypt.BCryptGenRandom.restype = ctypes.c_long


def wipe(buffer):
    if buffer:
        native = (ctypes.c_ubyte * len(buffer)).from_buffer(buffer)
        ctypes.memset(ctypes.addressof(native), 0, len(buffer))


def transform(value, protect):
    # from_buffer aliases an owned mutable bytearray; no plaintext bytes/string copy.
    native_input = (ctypes.c_ubyte * len(value)).from_buffer(value)
    source = Blob(len(value), ctypes.addressof(native_input))
    destination = Blob()
    function = crypt32.CryptProtectData if protect else crypt32.CryptUnprotectData
    try:
        if not function(ctypes.byref(source), None, None, None, None, 1,
                        ctypes.byref(destination)):
            raise ctypes.WinError(ctypes.get_last_error())
        if destination.length > 65536:
            raise RuntimeError('Unexpected DPAPI output size.')
        output = bytearray(destination.length)
        if output:
            native_output = (ctypes.c_ubyte * len(output)).from_buffer(output)
            ctypes.memmove(ctypes.addressof(native_output), destination.data, len(output))
        return output
    finally:
        if destination.data:
            if destination.length <= 65536:
                ctypes.memset(destination.data, 0, destination.length)
            kernel32.LocalFree(destination.data)


CODES = {0: 'RuntimeVerified', 200: 'RuntimeDebuggerDetected',
         201: 'RuntimeNativeEntryChanged', 202: 'RuntimeInspectionError',
         203: 'UnsupportedRuntimePlatform', 204: 'RuntimeSessionBlocked',
         205: 'InvalidRuntimeConfiguration'}


def decision(phase):
    if not guarded:
        return True, 'Unguarded'
    code = guard.ig_runtime_check(phase)
    return code == 0, CODES.get(code, f'NativeStatus{code}')


def emit(operation, allowed, code, **extra):
    print(json.dumps(dict(operation=operation, guarded=guarded, allowed=allowed,
                          code=code, **extra)), flush=True)


allowed, code = decision(0)
encrypted = bytearray()
if allowed:
    generated = bytearray(60)
    native_generated = (ctypes.c_ubyte * len(generated)).from_buffer(generated)
    try:
        if bcrypt.BCryptGenRandom(None, ctypes.addressof(native_generated), len(generated), 2) != 0:
            raise RuntimeError('Synthetic random data generation failed.')
        generated[:12] = b'LAB_RUNTIME_'
        encrypted = transform(generated, True)
    finally:
        wipe(generated)
emit('startup', allowed, code)
try:
    for line in sys.stdin:
        command = line.strip()
        if command == 'exit':
            break
        if command == 'check':
            emit('check', *decision(1))
        elif command == 'use':
            allowed, code = decision(2)
            if not allowed:
                emit('use', allowed, code)
                continue
            plain = transform(encrypted, False)
            try:
                emit('use', True, code, bytes=len(plain),
                     plaintextSha256=hashlib.sha256(plain).hexdigest().upper())
            finally:
                wipe(plain)
        else:
            emit('command', False, 'InvalidCommand')
finally:
    wipe(encrypted)
