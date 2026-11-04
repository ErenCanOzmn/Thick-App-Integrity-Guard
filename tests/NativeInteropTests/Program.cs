using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegrityGuard;
using IntegrityGuardNative;

Api.Initialize(args.Length == 0 ? null : Path.GetFullPath(args[0]));
var tests = new (string, Action)[] {
    ("ABI version and binary snapshot ownership", Snapshot),
    ("failed recheck clears snapshots and latches session", Latch),
    ("strict config parsing", Config),
    ("invalid handles and double close", Handles),
    ("null pointers and buffer bounds", Bounds),
    ("empty verified files", Empty),
    ("native release floor", ReleaseFloor),
    ("signature rejection", Signature),
    ("missing file rejection", Missing),
    ("invalid checkpoint blocks session", Checkpoint),
    ("session resource limit and recovery", Limits),
    ("concurrent independent callers", Concurrent),
    ("runtime gate succeeds clean and cannot reset after invalid phase", RuntimeGate),
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} interop tests passed ({(args.Length == 0 ? "managed entrypoints" : "native DLL")}).");
return failed == 0 ? 0 : 1;

static void Expect(bool value, string message) { if (!value) throw new Exception(message); }

static void Snapshot()
{
    using var f = new Fixture([0, 1, 0, 255, 42]);
    Expect(Api.Version() == 1, "ABI mismatch");
    var h = f.Open();
    try
    {
        Expect(Api.Read(h).Code == 4, "read before check allowed");
        Expect(Api.Check(h, 0) == 0, "verify failed");
        File.WriteAllBytes(f.Payload, [9, 9]);
        var read = Api.Read(h);
        Expect(read.Code == 0 && read.Bytes.SequenceEqual(new byte[] { 0, 1, 0, 255, 42 }), "not a binary snapshot");
        read.Bytes[0] = 9;
        Expect(Api.Read(h).Bytes[0] == 0, "caller modified library snapshot");
        Expect(Api.Read(h, "other.bin").Code == 5, "unknown file allowed");
    }
    finally { Api.Close(h); }
}

static void Latch()
{
    using var f = new Fixture([1, 2]);
    var h = f.Open();
    try
    {
        Expect(Api.Check(h, 0) == 0, "initial verify failed");
        File.WriteAllBytes(f.Payload, [9]);
        Expect(Api.Check(h, 2) == 105, "tamper not rejected");
        Expect(Api.Read(h).Code == 4, "stale snapshot returned");
        File.WriteAllBytes(f.Payload, [1, 2]);
        Expect(Api.Check(h, 1) == 108, "latch reset");
    }
    finally { Api.Close(h); }
}

static void Config()
{
    using var f = new Fixture([1]);
    string[] invalid = ["{}", "null", "[]", "{", f.Config().Replace("\"minimumRelease\":1", "\"minimumRelease\":0"),
        f.Config().Replace("\"minimumRelease\":1", "\"minimumRelease\":1,\"minimumRelease\":2"),
        f.Config().Replace("\"minimumRelease\":1", "\"extra\":1,\"minimumRelease\":1"),
        JsonSerializer.Serialize(new { root = ".", publicKeyPem = f.PublicKey, requiredFiles = new[] { "payload.bin" }, minimumRelease = 1 }),
        JsonSerializer.Serialize(new { root = f.Root, publicKeyPem = f.PublicKey, requiredFiles = new[] { "../x" }, minimumRelease = 1 }),
        JsonSerializer.Serialize(new { root = f.Root, publicKeyPem = f.PublicKey, requiredFiles = new[] { "payload.bin" } })];
    foreach (var json in invalid)
    {
        var result = Api.Open(json);
        if (result.Handle != 0) Api.Close(result.Handle);
        Expect(result.Code == 100 && result.Handle == 0, "invalid config accepted: " + json);
    }
}

static void Handles()
{
    using var f = new Fixture([1]);
    Expect(Api.Check(0, 0) == 2 && Api.Close(ulong.MaxValue) == 2, "invalid handle accepted");
    var old = f.Open();
    Expect(Api.Close(old) == 0 && Api.Close(old) == 2 && Api.Check(old, 0) == 2, "closed handle accepted");
    var fresh = f.Open();
    try { Expect(fresh != old, "handle reused"); }
    finally { Api.Close(fresh); }
}

static unsafe void Bounds()
{
    ulong h = 99;
    Expect(Api.OpenPtr(null, 0, &h) == 1 && h == 0, "null config output");
    Expect(Api.OpenPtr(null, 0, null) == 1, "null handle output");
    byte one = 0;
    Expect(Api.OpenPtr(&one, 128 * 1024 + 1, &h) == 1, "oversize config accessed");
    using var f = new Fixture([1, 2, 3]);
    h = f.Open();
    try
    {
        Expect(Api.Check(h, 0) == 0, "verify failed");
        uint size = 99;
        byte[] name = Encoding.UTF8.GetBytes("payload.bin");
        fixed (byte* n = name)
        {
            Expect(Api.CopyPtr(h, n, (uint)name.Length, null, 0, &size) == 3 && size == 3, "size query failed");
            byte sentinel = 77;
            Expect(Api.CopyPtr(h, n, (uint)name.Length, &sentinel, 1, &size) == 3 && sentinel == 77,
                "short buffer was written");
            Expect(Api.CopyPtr(h, n, (uint)name.Length, null, 1, &size) == 1 && size == 0, "null output accepted");
            Expect(Api.CopyPtr(h, n, (uint)name.Length, null, 0, null) == 1, "null length accepted");
        }
    }
    finally { Api.Close(h); }
}

static void Empty()
{
    using var f = new Fixture([]);
    var h = f.Open();
    try { Expect(Api.Check(h, 0) == 0 && Api.Read(h).Bytes.Length == 0 && Api.Read(h).Code == 0, "empty file failed"); }
    finally { Api.Close(h); }
}

static void ReleaseFloor()
{
    using var f = new Fixture([1]);
    var h = f.Open(2);
    try { Expect(Api.Check(h, 0) == 104, "release floor ignored"); }
    finally { Api.Close(h); }
}

static void Signature()
{
    using var f = new Fixture([1]);
    var signature = File.ReadAllBytes(Path.Combine(f.Root, "integrity.sig"));
    signature[0] ^= 1;
    File.WriteAllBytes(Path.Combine(f.Root, "integrity.sig"), signature);
    var h = f.Open();
    try { Expect(Api.Check(h, 0) == 101 && Api.Read(h).Code == 4, "invalid signature accepted"); }
    finally { Api.Close(h); }
}

static void Missing()
{
    using var f = new Fixture([1]);
    File.Delete(f.Payload);
    var h = f.Open();
    try { Expect(Api.Check(h, 0) == 107, "missing file accepted"); }
    finally { Api.Close(h); }
}

static void Checkpoint()
{
    using var f = new Fixture([1]);
    var h = f.Open();
    try { Expect(Api.Check(h, 999) == 100 && Api.Check(h, 0) == 108, "invalid checkpoint did not latch"); }
    finally { Api.Close(h); }
}

static void Limits()
{
    using var f = new Fixture([1]);
    var handles = new List<ulong>();
    try
    {
        for (var i = 0; i < 16; i++) handles.Add(f.Open());
        var overflow = Api.Open(f.Config());
        Expect(overflow.Code == 6 && overflow.Handle == 0, "resource limit ignored");
    }
    finally { foreach (var h in handles) Api.Close(h); }
    var recovered = f.Open();
    Api.Close(recovered);
}

static void Concurrent()
{
    using var f = new Fixture([1, 2, 3]);
    Parallel.For(0, 8, _ => {
        var h = f.Open();
        try { Expect(Api.Check(h, 0) == 0 && Api.Read(h).Bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "concurrent failure"); }
        finally { Api.Close(h); }
    });
}

static void RuntimeGate()
{
    Expect(Api.RuntimeCheck(0) == 0, "clean runtime startup failed");
    Expect(Api.RuntimeCheck(2) == 0, "clean runtime pre-operation failed");
    Expect(Api.RuntimeCheck(999) == 205, "invalid runtime checkpoint not rejected");
    Expect(Api.RuntimeCheck(0) == 204, "runtime failure latch was reset");
}

static unsafe class Api
{
    private static delegate* unmanaged[Cdecl]<uint> VersionPtr;
    internal static delegate* unmanaged[Cdecl]<byte*, uint, ulong*, int> OpenPtr;
    private static delegate* unmanaged[Cdecl]<ulong, int, int> CheckPtr;
    internal static delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, uint*, int> CopyPtr;
    private static delegate* unmanaged[Cdecl]<ulong, int> ClosePtr;
    private static delegate* unmanaged[Cdecl]<int, int> RuntimeCheckPtr;

    internal static uint Version() => VersionPtr();
    internal static int Check(ulong handle, int checkpoint) => CheckPtr(handle, checkpoint);
    internal static int Close(ulong handle) => ClosePtr(handle);
    internal static int RuntimeCheck(int checkpoint) => RuntimeCheckPtr(checkpoint);

    internal static void Initialize(string? path)
    {
        if (path is null)
        {
            VersionPtr = &Exports.AbiVersion; OpenPtr = &Exports.Open; CheckPtr = &Exports.Check;
            CopyPtr = &Exports.CopyFile; ClosePtr = &Exports.Close;
            RuntimeCheckPtr = &Exports.RuntimeCheck;
        }
        else
        {
            // NativeAOT must stay loaded until process exit.
            var library = NativeLibrary.Load(path);
            VersionPtr = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, "ig_abi_version");
            OpenPtr = (delegate* unmanaged[Cdecl]<byte*, uint, ulong*, int>)NativeLibrary.GetExport(library, "ig_open");
            CheckPtr = (delegate* unmanaged[Cdecl]<ulong, int, int>)NativeLibrary.GetExport(library, "ig_check");
            CopyPtr = (delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, uint*, int>)NativeLibrary.GetExport(library, "ig_copy_file");
            ClosePtr = (delegate* unmanaged[Cdecl]<ulong, int>)NativeLibrary.GetExport(library, "ig_close");
            RuntimeCheckPtr = (delegate* unmanaged[Cdecl]<int, int>)NativeLibrary.GetExport(library, "ig_runtime_check");
        }
    }

    internal static (int Code, ulong Handle) Open(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ulong handle = 0;
        fixed (byte* p = bytes) { var code = OpenPtr(p, (uint)bytes.Length, &handle); return (code, handle); }
    }

    internal static (int Code, byte[] Bytes) Read(ulong handle, string name = "payload.bin")
    {
        var names = Encoding.UTF8.GetBytes(name);
        uint size;
        fixed (byte* p = names)
        {
            var code = CopyPtr(handle, p, (uint)names.Length, null, 0, &size);
            if (code != 0 && code != 3) return (code, []);
            var bytes = new byte[size];
            fixed (byte* output = bytes)
                code = CopyPtr(handle, p, (uint)names.Length, output, size, &size);
            return (code, bytes);
        }
    }
}

sealed class Fixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "IntegrityGuard-ABI-" + Guid.NewGuid().ToString("N"));
    internal string Payload => Path.Combine(Root, "payload.bin");
    internal string PublicKey { get; }
    internal Fixture(byte[] payload)
    {
        Directory.CreateDirectory(Root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = key.ExportSubjectPublicKeyInfoPem();
        File.WriteAllBytes(Payload, payload);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1,
            new Dictionary<string, string> { ["payload.bin"] = Convert.ToHexString(SHA256.HashData(payload)) }));
        File.WriteAllBytes(Path.Combine(Root, "integrity.json"), manifest);
        File.WriteAllBytes(Path.Combine(Root, "integrity.sig"), key.SignData(manifest,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    internal string Config(long floor = 1) => JsonSerializer.Serialize(new { root = Root,
        publicKeyPem = PublicKey, requiredFiles = new[] { "payload.bin" }, minimumRelease = floor });
    internal ulong Open(long floor = 1)
    {
        var result = Api.Open(Config(floor));
        if (result.Code != 0) throw new Exception("open failed " + result.Code);
        return result.Handle;
    }
    public void Dispose() { Directory.Delete(Root, true); }
}
