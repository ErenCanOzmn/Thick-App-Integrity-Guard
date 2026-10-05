using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntegrityGuard;

public sealed record Manifest(int Version, Dictionary<string, string> Files, long Release = 1);
public sealed record VerificationResult(bool Success, string Code,
    IReadOnlyDictionary<string, byte[]> Files);

/// <summary>Authenticates bounded file snapshots against a signed manifest.
/// This is not a process-memory or instrumentation detector.</summary>
public static class IntegrityVerifier
{
    public const int MaxFileBytes = 32 * 1024 * 1024;
    public const int MaxTotalBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8
    };
    private static readonly ManifestJsonContext JsonContext = new(JsonOptions);

    public static VerificationResult Verify(string? root, string? publicKeyPem,
        IReadOnlyCollection<string>? requiredFiles, long minimumRelease = 1)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(publicKeyPem) ||
            requiredFiles is null || minimumRelease < 1) return Fail("InvalidConfiguration");
        try
        {
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in requiredFiles)
                if (!SafeName(name) || !expected.Add(name)) return Fail("InvalidConfiguration");
            if (expected.Count == 0 || expected.Count > 32) return Fail("InvalidConfiguration");

            root = Path.GetFullPath(root);
            var manifestBytes = ReadBounded(Path.Combine(root, "integrity.json"), 64 * 1024);
            var signature = ReadBounded(Path.Combine(root, "integrity.sig"), 256);
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256 || !key.VerifyData(manifestBytes, signature,
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Fail("InvalidSignature");

            // Reject duplicate JSON properties rather than accepting last-value-wins parsing.
            using (var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 8 }))
                if (HasDuplicateProperties(document.RootElement)) return Fail("InvalidManifest");
            var manifest = JsonSerializer.Deserialize(manifestBytes, JsonContext.Manifest);
            if (manifest is null || manifest.Version != 1 || manifest.Files is null ||
                manifest.Release < 1 || manifest.Files.Count != expected.Count) return Fail("InvalidManifest");
            // This floor must be provided by a trusted host or service. This verifier
            // intentionally does not persist a rollbackable local 'highest seen' value.
            if (manifest.Release < minimumRelease) return Fail("RollbackRejected");

            var snapshots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var remaining = MaxTotalBytes;
            foreach (var entry in manifest.Files)
            {
                if (!SafeName(entry.Key) || !expected.Remove(entry.Key)) return Fail("InvalidManifest");
                if (entry.Value is null || entry.Value.Length != 64 ||
                    !entry.Value.All(char.IsAsciiHexDigit)) return Fail("InvalidDigest");
                var expectedHash = Convert.FromHexString(entry.Value);
                var bytes = ReadBounded(Path.Combine(root, entry.Key), Math.Min(remaining, MaxFileBytes));
                remaining -= bytes.Length;
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), expectedHash))
                    return Fail("FileMismatch");
                snapshots.Add(entry.Key, bytes);
            }
            if (expected.Count != 0) return Fail("MissingRequiredFile");
            return new(true, "Verified", snapshots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException or ArgumentException or NotSupportedException)
        {
            return Fail("VerificationError");
        }
    }

    internal static bool SafeName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 128 && name is not "." and not ".." &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') &&
        !name.EndsWith('.');

    private static VerificationResult Fail(string code) =>
        new(false, code, new Dictionary<string, byte[]>());

    private static bool HasDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in node.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray())
                if (HasDuplicateProperties(child)) return true;
        return false;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        // Deny writes/deletion while taking the snapshot; callers must consume the snapshot.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new IOException("File exceeds size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("File changed during read.");
        return bytes;
    }
}

[JsonSerializable(typeof(Manifest), GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class ManifestJsonContext : JsonSerializerContext { }
