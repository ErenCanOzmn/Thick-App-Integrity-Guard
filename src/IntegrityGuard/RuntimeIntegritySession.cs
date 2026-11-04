using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace IntegrityGuard;

public sealed record NativeEntryPoint(string Module, string Export);
public sealed record RuntimeFinding(string Code, string Module, string Export);
public sealed record RuntimeVerificationResult(bool Success, string Code,
    IReadOnlyList<RuntimeFinding> Findings);

/// <summary>Local Windows x64 anti-instrumentation policy. Not remote attestation.</summary>
public sealed class RuntimeIntegrityPolicy
{
    public IReadOnlyList<NativeEntryPoint> EntryPoints { get; }
    public bool DetectDebugger { get; }

    public RuntimeIntegrityPolicy(IEnumerable<NativeEntryPoint>? entryPoints = null,
        bool detectDebugger = true)
    {
        var targets = (entryPoints ?? new[]
        {
            new NativeEntryPoint("crypt32.dll", "CryptProtectData"),
            new NativeEntryPoint("crypt32.dll", "CryptUnprotectData"),
            new NativeEntryPoint("bcrypt.dll", "BCryptFinishHash"),
            new NativeEntryPoint("ncrypt.dll", "NCryptVerifySignature")
        }).Take(33).ToArray();
        if (targets.Length is < 1 or > 32 || targets.Any(t => t is null ||
            !IntegrityVerifier.SafeName(t.Module) || !t.Module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(t.Export) || t.Export.Length > 128 ||
            !t.Export.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '?')) ||
            targets.Select(t => t.Module.ToUpperInvariant() + "!" + t.Export).Distinct().Count() != targets.Length)
            throw new ArgumentException("Provide 1 to 32 distinct system DLL/export names.", nameof(entryPoints));
        EntryPoints = Array.AsReadOnly(targets);
        DetectDebugger = detectDebugger;
    }
}

/// <summary>Checks debugger state and native entry bytes against the backing system PE.
/// First failure latches. The caller owns scheduling and must gate the sensitive operation.
/// This raises the cost of conventional inline hooks; it is not an isolation boundary.</summary>
public sealed class RuntimeIntegritySession
{
    public const int EntryBytes = 32;
    private readonly RuntimeIntegrityPolicy policy;
    private readonly object gate = new();
    private bool blocked;

    // Keep references for the process lifetime: export pointers must remain mapped.
    private static readonly object ModulesGate = new();
    private static readonly Dictionary<string, nint> Modules = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeIntegritySession(RuntimeIntegrityPolicy? policy = null) =>
        this.policy = policy ?? new RuntimeIntegrityPolicy();

    public bool IsBlocked { get { lock (gate) return blocked; } }

    public RuntimeVerificationResult Check(IntegrityCheckpoint checkpoint)
    {
        lock (gate)
        {
            if (blocked) return Failure("RuntimeSessionBlocked");
            RuntimeVerificationResult result;
            if (!Enum.IsDefined(checkpoint)) result = Failure("InvalidRuntimeConfiguration");
            else if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                result = Failure("UnsupportedRuntimePlatform");
            else
            {
                try { result = Inspect(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    ArgumentException or BadImageFormatException or InvalidOperationException or
                    DllNotFoundException or EntryPointNotFoundException or NotSupportedException or OverflowException)
                { result = Failure("RuntimeInspectionError"); }
            }
            if (!result.Success) blocked = true;
            return result;
        }
    }

    private RuntimeVerificationResult Inspect()
    {
        if (policy.DetectDebugger)
        {
            if (!CheckRemoteDebuggerPresent(new nint(-1), out var remote))
                return Failure("RuntimeInspectionError");
            if (Debugger.IsAttached || IsDebuggerPresent() || remote)
                return Failure("RuntimeDebuggerDetected");
        }
        var findings = new List<RuntimeFinding>();
        foreach (var target in policy.EntryPoints)
        {
            var code = InspectEntry(target);
            if (code != "RuntimeVerified") findings.Add(new(code, target.Module, target.Export));
        }
        return findings.Count == 0
            ? new(true, "RuntimeVerified", Array.Empty<RuntimeFinding>())
            : new(false, findings[0].Code, findings.AsReadOnly());
    }

    private static string InspectEntry(NativeEntryPoint target)
    {
        var systemDirectory = Path.GetFullPath(Environment.SystemDirectory);
        nint module;
        lock (ModulesGate)
        {
            if (!Modules.TryGetValue(target.Module, out module))
            {
                module = NativeLibrary.Load(Path.Combine(systemDirectory, target.Module),
                    typeof(RuntimeIntegritySession).Assembly, DllImportSearchPath.System32);
                Modules.Add(target.Module, module);
            }
        }
        if (!NativeLibrary.TryGetExport(module, target.Export, out var address))
            return "RuntimeExportUnavailable";
        // Forwarded exports can resolve into another DLL. Compare the actual owner,
        // not the facade module's file offset.
        if (!GetModuleHandleExW(0x00000004 | 0x00000002, address, out var owner))
            return "RuntimeOwnerUnavailable";
        var pathBuffer = new StringBuilder(32768);
        var chars = GetModuleFileNameW(owner, pathBuffer, pathBuffer.Capacity);
        if (chars == 0 || chars >= pathBuffer.Capacity) return "RuntimeOwnerUnavailable";
        var path = Path.GetFullPath(pathBuffer.ToString());
        if (!string.Equals(Path.GetDirectoryName(path), systemDirectory, StringComparison.OrdinalIgnoreCase))
            return "RuntimeUnexpectedModule";
        var rva = checked((int)(address.ToInt64() - owner.ToInt64()));
        if (rva < 0) return "RuntimeOwnerUnavailable";

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader is not { } header)
            return "RuntimeImageUnsupported";
        var section = pe.PEHeaders.SectionHeaders.FirstOrDefault(s => rva >= s.VirtualAddress &&
            (long)rva + EntryBytes <= (long)s.VirtualAddress + s.SizeOfRawData);
        if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0)
            return "RuntimeImageUnsupported";
        if (RelocationOverlaps(pe, header.BaseRelocationTableDirectory, rva, EntryBytes))
            return "RuntimeRelocationUnsupported";
        var expected = pe.GetSectionData(rva).GetContent(0, EntryBytes).ToArray();
        var actual = new byte[EntryBytes];
        if (!ReadProcessMemory(new nint(-1), address, actual, (nuint)actual.Length, out var read) ||
            read != (nuint)actual.Length) return "RuntimeEntryUnreadable";
        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? "RuntimeVerified" : "RuntimeNativeEntryChanged";
    }

    private static bool RelocationOverlaps(PEReader pe, DirectoryEntry directory, int start, int count)
    {
        if (directory.RelativeVirtualAddress == 0 || directory.Size == 0) return false;
        if (directory.Size is < 0 or > 4 * 1024 * 1024) throw new BadImageFormatException();
        var bytes = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0, directory.Size).ToArray();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw new BadImageFormatException();
            var page = BitConverter.ToUInt32(bytes, offset);
            var blockSize = BitConverter.ToUInt32(bytes, offset + 4);
            if (blockSize < 8 || (blockSize & 1) != 0 || blockSize > bytes.Length - offset)
                throw new BadImageFormatException();
            for (var i = offset + 8; i < offset + blockSize; i += 2)
            {
                var entry = BitConverter.ToUInt16(bytes, i);
                var type = entry >> 12;
                if (type == 0) continue;
                var location = (long)page + (entry & 0xFFF);
                // x64 DIR64 is 8 bytes. Conservatively reject an intersecting
                // unknown relocation too; never silently skip bytes under comparison.
                const int width = 8;
                if (location < (long)start + count && location + width > start) return true;
            }
            offset = checked(offset + (int)blockSize);
        }
        return false;
    }

    private static RuntimeVerificationResult Failure(string code) => new(false, code, Array.Empty<RuntimeFinding>());

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(nint process, [MarshalAs(UnmanagedType.Bool)] out bool present);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint module, StringBuilder path, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer,
        nuint size, out nuint read);
}
