using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using IntegrityGuard;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    Console.Error.WriteLine("This laboratory requires Windows x64.");
    return 2;
}
if (args.Any(a => a != "--unguarded")) return 2;
var guarded = !args.Contains("--unguarded");
// Load monitored DLLs before announcing readiness so a subsequent attach can
// observe the same exports. No external network, files, or real credentials.
foreach (var dll in new[] { "crypt32.dll", "bcrypt.dll", "ncrypt.dll" })
    _ = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, dll),
        typeof(RuntimeIntegritySession).Assembly, DllImportSearchPath.System32);
var session = guarded ? new RuntimeIntegritySession() : null;
var startup = Decision(IntegrityCheckpoint.Startup);
byte[] encrypted = Array.Empty<byte>();
if (startup.Success)
{
    // Keep the generated plaintext in owned byte buffers, not an immutable string.
    var generated = new byte[60];
    "LAB_RUNTIME_"u8.CopyTo(generated);
    RandomNumberGenerator.Fill(generated.AsSpan(12));
    try { encrypted = Dpapi.Protect(generated); }
    finally { CryptographicOperations.ZeroMemory(generated); }
}
Emit("startup", startup);
string? line;
while ((line = Console.ReadLine()) is not null)
{
    if (line == "exit") return 0;
    if (line == "check") Emit("check", Decision(IntegrityCheckpoint.Periodic));
    else if (line == "use")
    {
        var result = Decision(IntegrityCheckpoint.BeforeOperation);
        if (!result.Success) { Emit("use", result); continue; }
        var plain = Dpapi.Unprotect(encrypted);
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new { operation = "use", guarded,
                allowed = true, code = result.Code, bytes = plain.Length,
                plaintextSha256 = Convert.ToHexString(SHA256.HashData(plain)) }));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    else Emit("command", new(false, "InvalidCommand", Array.Empty<RuntimeFinding>()));
}
return 0;

RuntimeVerificationResult Decision(IntegrityCheckpoint phase) => session?.Check(phase) ??
    new(true, "Unguarded", Array.Empty<RuntimeFinding>());

void Emit(string operation, RuntimeVerificationResult result) => Console.WriteLine(JsonSerializer.Serialize(
    new { operation, guarded, allowed = result.Success, code = result.Code, findings = result.Findings }));

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { internal int Length; internal nint Data; }

    internal static byte[] Protect(byte[] value) => Transform(value, true);
    internal static byte[] Unprotect(byte[] value) => Transform(value, false);

    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var ok = protect
                ? CryptProtectData(ref input, null, 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Length is < 0 or > 65536) throw new CryptographicException("Invalid DPAPI output.");
            var bytes = new byte[output.Length];
            Marshal.Copy(output.Data, bytes, 0, output.Length);
            return bytes;
        }
        finally
        {
            // Own both native buffers. Clearing these does not erase an earlier capture.
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0)
            {
                if (output.Length is >= 0 and <= 65536)
                    for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description,
        nint entropy, nint reserved, nint prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description,
        nint entropy, nint reserved, nint prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
