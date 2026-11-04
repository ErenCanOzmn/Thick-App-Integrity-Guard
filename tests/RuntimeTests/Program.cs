using System.Runtime.InteropServices;
using IntegrityGuard;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    var unsupported = new RuntimeIntegritySession().Check(IntegrityCheckpoint.Startup);
    return !unsupported.Success && unsupported.Code == "UnsupportedRuntimePlatform" ? 0 : 1;
}
var tests = new (string Name, Action Body)[]
{
    ("clean system API entries match their backing PE", CleanEntries),
    ("missing export fails closed and latches", MissingExport),
    ("invalid checkpoint blocks runtime session", InvalidCheckpoint),
    ("policy copies the caller target list", CopiesPolicy),
    ("policy rejects empty unsafe duplicate and excessive targets", InvalidPolicies),
    ("entry-byte modification is detected and repair does not unlock session", EntryChange),
    ("parallel checks remain independent and successful", ConcurrentChecks)
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} runtime tests passed.");
return failures == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void CleanEntries()
{
    var session = new RuntimeIntegritySession();
    foreach (var phase in Enum.GetValues<IntegrityCheckpoint>())
    {
        var result = session.Check(phase);
        Require(result.Success && result.Code == "RuntimeVerified" && result.Findings.Count == 0,
            result.Code + " " + string.Join(",", result.Findings));
    }
}

static void MissingExport()
{
    var session = new RuntimeIntegritySession(new RuntimeIntegrityPolicy(
        [new("crypt32.dll", "IntegrityGuardNonexistentExport")]));
    var first = session.Check(IntegrityCheckpoint.Startup);
    Require(!first.Success && first.Code == "RuntimeExportUnavailable" && session.IsBlocked,
        "missing export accepted or did not latch");
    Require(session.Check(IntegrityCheckpoint.Periodic).Code == "RuntimeSessionBlocked", "latch lost");
}

static void InvalidCheckpoint()
{
    var session = new RuntimeIntegritySession();
    Require(session.Check((IntegrityCheckpoint)99).Code == "InvalidRuntimeConfiguration" && session.IsBlocked,
        "unknown checkpoint accepted");
}

static void CopiesPolicy()
{
    NativeEntryPoint[] targets = [new("crypt32.dll", "CryptUnprotectData")];
    var policy = new RuntimeIntegrityPolicy(targets);
    targets[0] = new("crypt32.dll", "IntegrityGuardNonexistentExport");
    Require(new RuntimeIntegritySession(policy).Check(IntegrityCheckpoint.Startup).Success,
        "caller mutation changed target policy");
}

static void InvalidPolicies()
{
    NativeEntryPoint[][] cases = [[], [new("../crypt32.dll", "CryptUnprotectData")],
        [new("crypt32.dll", "")], [new("crypt32.dll", "a"), new("CRYPT32.DLL", "a")],
        Enumerable.Range(0, 33).Select(i => new NativeEntryPoint("crypt32.dll", "entry" + i)).ToArray()];
    foreach (var targets in cases)
    {
        var rejected = false;
        try { _ = new RuntimeIntegrityPolicy(targets); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "invalid target policy accepted");
    }
}

static void EntryChange()
{
    var session = new RuntimeIntegritySession();
    Require(session.Check(IntegrityCheckpoint.Startup).Success, "clean startup failed");
    // This fixture modifies ONE byte in this test process only. It never calls
    // the modified function and restores it in finally, including page permissions.
    var module = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "crypt32.dll"));
    var entry = NativeLibrary.GetExport(module, "CryptUnprotectData");
    var original = Marshal.ReadByte(entry);
    uint previous = 0;
    try
    {
        Require(Native.VirtualProtect(entry, 1, 0x40, out previous), "cannot make fixture writable");
        Marshal.WriteByte(entry, (byte)(original ^ 1));
        Require(Native.FlushInstructionCache(new nint(-1), entry, 1), "cannot flush fixture instruction");
        var changed = session.Check(IntegrityCheckpoint.BeforeOperation);
        Require(!changed.Success && changed.Code == "RuntimeNativeEntryChanged" &&
            changed.Findings.Any(f => f.Export == "CryptUnprotectData"), "modified entry accepted");
    }
    finally
    {
        if (previous != 0)
        {
            Marshal.WriteByte(entry, original);
            _ = Native.FlushInstructionCache(new nint(-1), entry, 1);
            _ = Native.VirtualProtect(entry, 1, previous, out _);
        }
    }
    Require(session.Check(IntegrityCheckpoint.BeforeOperation).Code == "RuntimeSessionBlocked",
        "restoring bytes silently opened the old session");
    Require(new RuntimeIntegritySession().Check(IntegrityCheckpoint.Startup).Success,
        "restored clean entry did not pass a new session");
}

static void ConcurrentChecks()
{
    var session = new RuntimeIntegritySession();
    var results = new System.Collections.Concurrent.ConcurrentBag<RuntimeVerificationResult>();
    Parallel.For(0, 8, _ => results.Add(session.Check(IntegrityCheckpoint.Periodic)));
    Require(results.Count == 8 && results.All(r => r.Success), "parallel runtime check failed");
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
