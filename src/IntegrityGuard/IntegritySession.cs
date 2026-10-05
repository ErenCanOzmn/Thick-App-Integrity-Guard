using System.Collections.ObjectModel;
using System.Diagnostics;

namespace IntegrityGuard;

public enum IntegrityCheckpoint { Startup, Periodic, BeforeOperation }

/// <summary>Non-sensitive local telemetry, not an attestation or authorization token.</summary>
public sealed record IntegrityObservation(int SchemaVersion, Guid CheckId,
    DateTimeOffset TimestampUtc, IntegrityCheckpoint Checkpoint, string Code,
    bool Allowed, double DurationMilliseconds);

/// <summary>Immutable policy. Construct once from trusted application configuration.</summary>
public sealed class IntegrityPolicy
{
    public string Root { get; }
    public string PublicKeyPem { get; }
    public IReadOnlyList<string> RequiredFiles { get; }
    public long MinimumRelease { get; }

    public IntegrityPolicy(string root, string publicKeyPem,
        IEnumerable<string> requiredFiles, long minimumRelease = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        ArgumentNullException.ThrowIfNull(requiredFiles);
        if (minimumRelease < 1) throw new ArgumentOutOfRangeException(nameof(minimumRelease));
        var names = requiredFiles.Take(33).ToArray();
        if (names.Length is < 1 or > 32 || names.Any(n => !IntegrityVerifier.SafeName(n)) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("Provide 1 to 32 distinct flat filenames.", nameof(requiredFiles));
        Root = Path.GetFullPath(root);
        PublicKeyPem = publicKeyPem;
        RequiredFiles = Array.AsReadOnly(names);
        MinimumRelease = minimumRelease;
    }
}

/// <summary>One instance per application session. Failure is latched until restart.
/// Checks are serialized; host code owns scheduling and protected operations.</summary>
public sealed class IntegritySession
{
    private readonly IntegrityPolicy policy;
    private readonly Action<IntegrityObservation>? observer;
    private readonly object gate = new();
    private bool blocked;
    private long observerFailures;

    public IntegritySession(IntegrityPolicy policy, Action<IntegrityObservation>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        this.policy = policy;
        this.observer = observer;
    }

    public bool IsBlocked { get { lock (gate) return blocked; } }
    public long ObserverFailureCount => Interlocked.Read(ref observerFailures);

    public VerificationResult Check(IntegrityCheckpoint checkpoint)
    {
        VerificationResult result;
        var started = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (!Enum.IsDefined(checkpoint))
                result = Failure("InvalidConfiguration");
            else if (blocked)
                result = Failure("SessionBlocked");
            else
                result = IntegrityVerifier.Verify(policy.Root, policy.PublicKeyPem,
                    policy.RequiredFiles, policy.MinimumRelease);
            if (!result.Success) blocked = true;
        }
        // A telemetry failure must never grant permission or mask the verification result.
        // Call outside the lock. Hosts should enqueue quickly rather than perform network I/O.
        if (observer is not null)
        {
            var observation = new IntegrityObservation(1, Guid.NewGuid(), DateTimeOffset.UtcNow,
                checkpoint, result.Code, result.Success,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            try { observer(observation); }
            catch (Exception) { Interlocked.Increment(ref observerFailures); }
        }
        return result;
    }

    private static VerificationResult Failure(string code) => new(false, code,
        new ReadOnlyDictionary<string, byte[]>(new Dictionary<string, byte[]>()));
}
