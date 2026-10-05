using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegrityGuard;

var tests = new (string Name, Action Body)[]
{
    ("valid manifest returns authenticated snapshot", ValidManifestReturnsSnapshot),
    ("modified content fails closed", ModifiedContentFails),
    ("modified signature fails closed", ModifiedSignatureFails),
    ("signed malformed digest does not escape verifier", MalformedDigestFailsSafely),
    ("case-insensitive duplicate file properties are rejected", DuplicateFilePropertyFails),
    ("null required-file configuration fails closed", NullConfigurationFailsSafely),
    ("wrong verification key fails closed", WrongKeyFails),
    ("unsafe required filename is rejected", UnsafeRequiredNameFails),
    ("malformed public key does not escape verifier", MalformedPublicKeyFailsSafely),
    ("signed manifest with unknown member fails closed", UnknownManifestMemberFails),
    ("previously signed release cannot be replayed", SignedReleaseRollbackFails),
    ("non-positive signed release is rejected", NonPositiveReleaseFails),
    ("non-positive minimum release is invalid configuration", NonPositiveMinimumReleaseFails),
    ("session remains blocked after files are repaired", SessionRemainsBlocked),
    ("throwing observer never changes verification decisions", ObserverFailureIsIsolated),
    ("policy copies caller-owned filenames", PolicyCopiesNames),
    ("policy rejects empty duplicate unsafe and excessive filenames", PolicyRejectsInvalidNames),
    ("session enforces release policy", SessionEnforcesRelease),
    ("concurrent checks produce independent snapshots and events", ConcurrentChecks),
    ("unknown checkpoint fails closed", UnknownCheckpointFails),
    ("missing protected file blocks session", MissingFileBlocksSession),
    ("customer example consumes signed business data", CustomerReadsRules),
    ("customer example latches invalid business schema", CustomerRejectsInvalidRules),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} review tests passed.");
return failures == 0 ? 0 : 1;

static void ValidManifestReturnsSnapshot()
{
    using var fixture = new Fixture("original");
    var result = fixture.Verify();
    Require(result.Success, result.Code);
    Require(Encoding.UTF8.GetString(result.Files[Fixture.FileName]) == "original", "snapshot mismatch");

    File.WriteAllText(fixture.FilePath, "changed after verification");
    Require(Encoding.UTF8.GetString(result.Files[Fixture.FileName]) == "original",
        "verified snapshot changed after the source file changed");
}

static void ModifiedContentFails()
{
    using var fixture = new Fixture("original");
    File.WriteAllText(fixture.FilePath, "modified");
    var result = fixture.Verify();
    Require(!result.Success && result.Code == "FileMismatch", $"unexpected result: {result.Code}");
}

static void ModifiedSignatureFails()
{
    using var fixture = new Fixture("original");
    var signaturePath = Path.Combine(fixture.Root, "integrity.sig");
    var signature = File.ReadAllBytes(signaturePath);
    signature[0] ^= 0x01;
    File.WriteAllBytes(signaturePath, signature);
    var result = fixture.Verify();
    Require(!result.Success && result.Code == "InvalidSignature", $"unexpected result: {result.Code}");
}

static void MalformedDigestFailsSafely()
{
    using var fixture = new Fixture("original");
    var malformed = JsonSerializer.SerializeToUtf8Bytes(
        new Manifest(1, new Dictionary<string, string> { [Fixture.FileName] = new('Z', 64) }));
    fixture.WriteSignedManifest(malformed);
    var result = fixture.Verify();
    Require(!result.Success, "malformed digest was accepted");
}

static void DuplicateFilePropertyFails()
{
    using var fixture = new Fixture("original");
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("original")));
    var json = $"{{\"Version\":1,\"Files\":{{\"{Fixture.FileName}\":\"{digest}\",\"PAYLOAD.BIN\":\"{digest}\"}}}}";
    fixture.WriteSignedManifest(Encoding.UTF8.GetBytes(json));
    var result = fixture.Verify();
    Require(!result.Success && result.Code == "InvalidManifest", $"unexpected result: {result.Code}");
}

static void NullConfigurationFailsSafely()
{
    using var fixture = new Fixture("original");
    var result = IntegrityVerifier.Verify(fixture.Root, fixture.PublicKey, null!);
    Require(!result.Success && result.Code == "InvalidConfiguration", $"unexpected result: {result.Code}");
}

static void WrongKeyFails()
{
    using var fixture = new Fixture("original");
    using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var result = IntegrityVerifier.Verify(fixture.Root,
        otherKey.ExportSubjectPublicKeyInfoPem(), [Fixture.FileName]);
    Require(!result.Success && result.Code == "InvalidSignature", $"unexpected result: {result.Code}");
}

static void UnsafeRequiredNameFails()
{
    using var fixture = new Fixture("original");
    var result = IntegrityVerifier.Verify(fixture.Root, fixture.PublicKey, ["../payload.bin"]);
    Require(!result.Success && result.Code == "InvalidConfiguration", $"unexpected result: {result.Code}");
}

static void MalformedPublicKeyFailsSafely()
{
    using var fixture = new Fixture("original");
    var result = IntegrityVerifier.Verify(fixture.Root, "not a PEM key", [Fixture.FileName]);
    Require(!result.Success && result.Code == "VerificationError", $"unexpected result: {result.Code}");
}

static void UnknownManifestMemberFails()
{
    using var fixture = new Fixture("original");
    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("original")));
    var json = $"{{\"Version\":1,\"Files\":{{\"{Fixture.FileName}\":\"{digest}\"}},\"Extra\":true}}";
    fixture.WriteSignedManifest(Encoding.UTF8.GetBytes(json));
    var result = fixture.Verify();
    Require(!result.Success, "unknown manifest member was accepted");
}

static void SignedReleaseRollbackFails()
{
    using var fixture = new Fixture("release-one");
    var oldFile = File.ReadAllBytes(fixture.FilePath);
    var oldManifest = File.ReadAllBytes(Path.Combine(fixture.Root, "integrity.json"));
    var oldSignature = File.ReadAllBytes(Path.Combine(fixture.Root, "integrity.sig"));

    fixture.SetContentAndSign("release-two", release: 2);
    Require(fixture.Verify(minimumRelease: 2).Success, "new release did not verify");

    File.WriteAllBytes(fixture.FilePath, oldFile);
    File.WriteAllBytes(Path.Combine(fixture.Root, "integrity.json"), oldManifest);
    File.WriteAllBytes(Path.Combine(fixture.Root, "integrity.sig"), oldSignature);

    var replay = fixture.Verify(minimumRelease: 2);
    Require(!replay.Success && replay.Code == "RollbackRejected",
        $"unexpected rollback result: {replay.Code}");
}

static void NonPositiveReleaseFails()
{
    using var fixture = new Fixture("original");
    fixture.SetContentAndSign("invalid-release", release: 0);
    var result = fixture.Verify();
    Require(!result.Success && result.Code == "InvalidManifest", $"unexpected result: {result.Code}");
}

static void NonPositiveMinimumReleaseFails()
{
    using var fixture = new Fixture("original");
    var result = fixture.Verify(minimumRelease: 0);
    Require(!result.Success && result.Code == "InvalidConfiguration", $"unexpected result: {result.Code}");
}

static IntegritySession Session(Fixture fixture, Action<IntegrityObservation>? observer = null,
    long minimumRelease = 1) => new(new IntegrityPolicy(fixture.Root, fixture.PublicKey,
        [Fixture.FileName], minimumRelease), observer);

static void SessionRemainsBlocked()
{
    using var fixture = new Fixture("original");
    var events = new List<IntegrityObservation>();
    var session = Session(fixture, events.Add);
    Require(session.Check(IntegrityCheckpoint.Startup).Success, "startup failed");
    File.WriteAllText(fixture.FilePath, "changed");
    var failed = session.Check(IntegrityCheckpoint.BeforeOperation);
    Require(!failed.Success && failed.Code == "FileMismatch" && failed.Files.Count == 0,
        "modified file accepted or returned");
    fixture.SetContentAndSign("original");
    var repaired = session.Check(IntegrityCheckpoint.Periodic);
    Require(!repaired.Success && repaired.Code == "SessionBlocked" && session.IsBlocked,
        "repair silently unlocked session");
    Require(Session(fixture).Check(IntegrityCheckpoint.Startup).Success, "fresh session failed");
    Require(events.Count == 3 && !events[2].Allowed && events[0].SchemaVersion == 1 &&
        events[1].Checkpoint == IntegrityCheckpoint.BeforeOperation, "incorrect event sequence");
}

static void ObserverFailureIsIsolated()
{
    using var fixture = new Fixture("original");
    var session = Session(fixture, _ => throw new IOException("log unavailable"));
    Require(session.Check(IntegrityCheckpoint.Startup).Success, "logger blocked valid data");
    File.WriteAllText(fixture.FilePath, "changed");
    Require(!session.Check(IntegrityCheckpoint.BeforeOperation).Success && session.IsBlocked,
        "logger changed failed result");
    Require(session.ObserverFailureCount == 2, "observer failures not counted");
}

static void PolicyCopiesNames()
{
    using var fixture = new Fixture("original");
    string[] names = [Fixture.FileName];
    var policy = new IntegrityPolicy(fixture.Root, fixture.PublicKey, names);
    names[0] = "other.bin";
    Require(new IntegritySession(policy).Check(IntegrityCheckpoint.Startup).Success,
        "caller mutation changed policy");
}

static void PolicyRejectsInvalidNames()
{
    using var fixture = new Fixture("original");
    string[][] invalid = [[], ["../payload.bin"], ["payload.bin", "PAYLOAD.BIN"],
        Enumerable.Range(0, 33).Select(i => $"file{i}.bin").ToArray()];
    foreach (var names in invalid)
    {
        var rejected = false;
        try { _ = new IntegrityPolicy(fixture.Root, fixture.PublicKey, names); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "invalid policy accepted");
    }
}

static void SessionEnforcesRelease()
{
    using var fixture = new Fixture("original");
    var result = Session(fixture, minimumRelease: 2).Check(IntegrityCheckpoint.Startup);
    Require(!result.Success && result.Code == "RollbackRejected", "floor ignored");
}

static void ConcurrentChecks()
{
    using var fixture = new Fixture("original");
    var events = new System.Collections.Concurrent.ConcurrentBag<IntegrityObservation>();
    var results = new System.Collections.Concurrent.ConcurrentBag<VerificationResult>();
    var session = Session(fixture, events.Add);
    Parallel.For(0, 8, _ => results.Add(session.Check(IntegrityCheckpoint.Periodic)));
    Require(results.Count == 8 && results.All(r => r.Success), "parallel verification failed");
    Require(events.Select(e => e.CheckId).Distinct().Count() == 8, "missing or duplicate check IDs");
    var snapshots = results.Select(r => r.Files[Fixture.FileName]).ToArray();
    snapshots[0][0] = 0;
    Require(snapshots.Skip(1).All(b => Encoding.UTF8.GetString(b) == "original"),
        "snapshots shared across calls");
}

static void UnknownCheckpointFails()
{
    using var fixture = new Fixture("original");
    var session = Session(fixture);
    var result = session.Check((IntegrityCheckpoint)999);
    Require(!result.Success && result.Code == "InvalidConfiguration" && session.IsBlocked,
        "invalid checkpoint allowed");
}

static void MissingFileBlocksSession()
{
    using var fixture = new Fixture("original");
    File.Delete(fixture.FilePath);
    var session = Session(fixture);
    var result = session.Check(IntegrityCheckpoint.Startup);
    Require(!result.Success && result.Files.Count == 0 && session.IsBlocked, "missing file allowed");
}

static void WriteCustomerRules(Fixture fixture, string json)
{
    var bytes = Encoding.UTF8.GetBytes(json);
    File.WriteAllBytes(Path.Combine(fixture.Root, "rules.json"), bytes);
    fixture.WriteSignedManifest(JsonSerializer.SerializeToUtf8Bytes(new Manifest(1,
        new Dictionary<string, string> { ["rules.json"] = Convert.ToHexString(SHA256.HashData(bytes)) })));
}

static void CustomerReadsRules()
{
    using var fixture = new Fixture("unused");
    WriteCustomerRules(fixture, "{\"Product\":\"Sample\",\"UnitPrice\":12.50}");
    var example = new CustomerIntegration.ProtectedRules(fixture.Root, fixture.PublicKey, 1);
    Require(example.CheckStartup().Success, "customer startup failed");
    Require(example.TryReadForOperation(out var rules, out _) && rules?.UnitPrice == 12.50m,
        "customer data not consumed");
    File.WriteAllText(Path.Combine(fixture.Root, "rules.json"), "{}");
    Require(!example.TryReadForOperation(out var rejected, out _) && rejected is null,
        "modified customer data consumed");
}

static void CustomerRejectsInvalidRules()
{
    foreach (var json in new[] { "{", "null", "{}", "{\"Product\":\"Sample\"}",
        "{\"Product\":\"Sample\",\"UnitPrice\":-1}" })
    {
        using var fixture = new Fixture("unused");
        WriteCustomerRules(fixture, json);
        var example = new CustomerIntegration.ProtectedRules(fixture.Root, fixture.PublicKey, 1);
        Require(!example.TryReadForOperation(out var rules, out var code) && rules is null &&
            code == "InvalidBusinessRules", "invalid business data accepted");
        WriteCustomerRules(fixture, "{\"Product\":\"Sample\",\"UnitPrice\":12.50}");
        Require(!example.TryReadForOperation(out _, out _), "invalid business state silently reset");
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Fixture : IDisposable
{
    internal const string FileName = "payload.bin";
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal Fixture(string content)
    {
        Root = Path.Combine(Path.GetTempPath(), "IntegrityGuardReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        File.WriteAllText(FilePath, content);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        WriteSignedManifest(JsonSerializer.SerializeToUtf8Bytes(
            new Manifest(1, new Dictionary<string, string> { [FileName] = digest })));
    }

    internal string Root { get; }
    internal string FilePath => Path.Combine(Root, FileName);
    internal string PublicKey => key.ExportSubjectPublicKeyInfoPem();

    internal VerificationResult Verify(long minimumRelease = 1) =>
        IntegrityVerifier.Verify(Root, PublicKey, [FileName], minimumRelease);

    internal void SetContentAndSign(string content, long release = 1)
    {
        File.WriteAllText(FilePath, content);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        WriteSignedManifest(JsonSerializer.SerializeToUtf8Bytes(
            new Manifest(1, new Dictionary<string, string> { [FileName] = digest }, release)));
    }

    internal void WriteSignedManifest(byte[] manifest)
    {
        File.WriteAllBytes(Path.Combine(Root, "integrity.json"), manifest);
        File.WriteAllBytes(Path.Combine(Root, "integrity.sig"), key.SignData(manifest,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Dispose()
    {
        key.Dispose();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
