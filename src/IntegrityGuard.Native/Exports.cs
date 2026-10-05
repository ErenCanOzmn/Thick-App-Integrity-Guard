using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntegrityGuard;

namespace IntegrityGuardNative;

// Stable ABI v1 codes; never expose exceptions or managed objects to callers.
public enum NativeStatus
{
    Ok = 0, InvalidArgument = 1, InvalidHandle = 2, BufferTooSmall = 3,
    NotVerified = 4, FileNotFound = 5, ResourceLimit = 6, InternalError = 7,
    InvalidConfiguration = 100, InvalidSignature = 101, InvalidManifest = 102,
    InvalidDigest = 103, RollbackRejected = 104, FileMismatch = 105,
    MissingRequiredFile = 106, VerificationError = 107, SessionBlocked = 108
}

public static unsafe class Exports
{
    private const uint MaxConfigBytes = 128 * 1024;
    private const int MaxSessions = 16;
    private static readonly object RegistryGate = new();
    private static readonly Dictionary<ulong, State> Sessions = new();
    private static ulong nextHandle;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed class State(IntegritySession session)
    {
        internal readonly object Gate = new();
        internal readonly IntegritySession Session = session;
        internal IReadOnlyDictionary<string, byte[]>? Files;
        internal bool Closed;
        internal bool Faulted;
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersion() => 1;

    [UnmanagedCallersOnly(EntryPoint = "ig_open", CallConvs = [typeof(CallConvCdecl)])]
    public static int Open(byte* json, uint length, ulong* handle)
    {
        if (handle == null) return (int)NativeStatus.InvalidArgument;
        *handle = 0;
        if (json == null || length == 0 || length > MaxConfigBytes)
            return (int)NativeStatus.InvalidArgument;
        try
        {
            var bytes = new ReadOnlySpan<byte>(json, (int)length);
            // Strict decoding and duplicate rejection make config parsing unambiguous.
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (int)NativeStatus.InvalidConfiguration;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) return (int)NativeStatus.InvalidConfiguration;
            var config = JsonSerializer.Deserialize(bytes, ConfigContext.Default.Configuration);
            if (config is null || config.Root is null || !Path.IsPathFullyQualified(config.Root) ||
                config.Root.Contains('\0') || config.PublicKeyPem is null || config.PublicKeyPem.Contains('\0') ||
                config.RequiredFiles is null)
                return (int)NativeStatus.InvalidConfiguration;
            var state = new State(new IntegritySession(new IntegrityPolicy(config.Root,
                config.PublicKeyPem, config.RequiredFiles, config.MinimumRelease)));
            lock (RegistryGate)
            {
                if (Sessions.Count >= MaxSessions || nextHandle == ulong.MaxValue)
                    return (int)NativeStatus.ResourceLimit;
                var id = ++nextHandle; // Never reuse stale handles.
                Sessions.Add(id, state);
                *handle = id;
            }
            return (int)NativeStatus.Ok;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NotSupportedException)
        { return (int)NativeStatus.InvalidConfiguration; }
        catch (Exception) { return (int)NativeStatus.InternalError; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_check", CallConvs = [typeof(CallConvCdecl)])]
    public static int Check(ulong handle, int checkpoint)
    {
        try
        {
            var state = Lookup(handle);
            if (state is null) return (int)NativeStatus.InvalidHandle;
            lock (state.Gate)
            {
                if (state.Closed) return (int)NativeStatus.InvalidHandle;
                state.Files = null; // No stale snapshot survives a new check.
                if (state.Faulted) return (int)NativeStatus.SessionBlocked;
                try
                {
                    var result = state.Session.Check((IntegrityCheckpoint)checkpoint);
                    if (result.Success) state.Files = result.Files;
                    return (int)Map(result.Code);
                }
                catch (Exception)
                {
                    state.Faulted = true;
                    return (int)NativeStatus.InternalError;
                }
            }
        }
        catch (Exception) { return (int)NativeStatus.InternalError; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_copy_file", CallConvs = [typeof(CallConvCdecl)])]
    public static int CopyFile(ulong handle, byte* name, uint nameLength,
        byte* destination, uint capacity, uint* written)
    {
        if (written == null) return (int)NativeStatus.InvalidArgument;
        *written = 0;
        if (name == null || nameLength is 0 or > 128 ||
            (destination == null && capacity != 0) || capacity > IntegrityVerifier.MaxFileBytes)
            return (int)NativeStatus.InvalidArgument;
        try
        {
            var filename = StrictUtf8.GetString(new ReadOnlySpan<byte>(name, (int)nameLength));
            var state = Lookup(handle);
            if (state is null) return (int)NativeStatus.InvalidHandle;
            lock (state.Gate)
            {
                if (state.Closed) return (int)NativeStatus.InvalidHandle;
                if (state.Files is null) return (int)NativeStatus.NotVerified;
                if (!state.Files.TryGetValue(filename, out var bytes)) return (int)NativeStatus.FileNotFound;
                *written = (uint)bytes.Length;
                if (capacity < bytes.Length) return (int)NativeStatus.BufferTooSmall;
                // Empty files succeed with NULL/0. No NUL terminator is appended.
                bytes.AsSpan().CopyTo(new Span<byte>(destination, (int)capacity));
                return (int)NativeStatus.Ok;
            }
        }
        catch (DecoderFallbackException) { return (int)NativeStatus.InvalidArgument; }
        catch (Exception) { return (int)NativeStatus.InternalError; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ig_close", CallConvs = [typeof(CallConvCdecl)])]
    public static int Close(ulong handle)
    {
        try
        {
            State? state;
            lock (RegistryGate)
                if (!Sessions.Remove(handle, out state)) return (int)NativeStatus.InvalidHandle;
            lock (state.Gate)
            {
                state.Closed = true;
                state.Files = null;
            }
            return (int)NativeStatus.Ok;
        }
        catch (Exception) { return (int)NativeStatus.InternalError; }
    }

    private static State? Lookup(ulong handle)
    { lock (RegistryGate) return Sessions.GetValueOrDefault(handle); }

    private static NativeStatus Map(string code) => code switch
    {
        "Verified" => NativeStatus.Ok,
        "InvalidConfiguration" => NativeStatus.InvalidConfiguration,
        "InvalidSignature" => NativeStatus.InvalidSignature,
        "InvalidManifest" => NativeStatus.InvalidManifest,
        "InvalidDigest" => NativeStatus.InvalidDigest,
        "RollbackRejected" => NativeStatus.RollbackRejected,
        "FileMismatch" => NativeStatus.FileMismatch,
        "MissingRequiredFile" => NativeStatus.MissingRequiredFile,
        "VerificationError" => NativeStatus.VerificationError,
        "SessionBlocked" => NativeStatus.SessionBlocked,
        _ => NativeStatus.InternalError
    };
}

internal sealed record Configuration(
    [property: JsonRequired] string Root,
    [property: JsonRequired] string PublicKeyPem,
    [property: JsonRequired] string[] RequiredFiles,
    [property: JsonRequired] long MinimumRelease);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Configuration))]
internal partial class ConfigContext : JsonSerializerContext { }
