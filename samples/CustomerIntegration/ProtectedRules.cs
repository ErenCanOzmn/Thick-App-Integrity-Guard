using System.Text.Json;
using System.Text.Json.Serialization;
using IntegrityGuard;

namespace CustomerIntegration;

// Host supplies a pinned public key and a trusted release floor, not user-editable settings.
// Register one instance per application session. No network service or background timer is hidden here.
public sealed class ProtectedRules
{
    private readonly IntegritySession session;
    private bool invalidRules;

    public ProtectedRules(string applicationDirectory, string pinnedPublicKey, long minimumRelease,
        Action<IntegrityObservation>? enqueueObservation = null)
    {
        session = new IntegritySession(new IntegrityPolicy(applicationDirectory,
            pinnedPublicKey, ["rules.json"], minimumRelease), enqueueObservation);
    }

    public VerificationResult CheckStartup() => session.Check(IntegrityCheckpoint.Startup);
    public VerificationResult CheckPeriodic() => session.Check(IntegrityCheckpoint.Periodic);

    // Call from the host's serialized UI/workflow context. Never reopen rules.json afterward.
    public bool TryReadForOperation(out Rules? rules, out string code)
    {
        rules = null;
        if (invalidRules) { code = "InvalidBusinessRules"; return false; }
        var result = session.Check(IntegrityCheckpoint.BeforeOperation);
        code = result.Code;
        if (!result.Success) return false;
        try
        {
            var candidate = JsonSerializer.Deserialize<Rules>(result.Files["rules.json"]);
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Product) ||
                candidate.UnitPrice is < 0 or > 1_000_000)
                throw new JsonException();
            rules = candidate;
            return true;
        }
        catch (JsonException)
        {
            invalidRules = true;
            code = "InvalidBusinessRules";
            return false;
        }
    }
}

public sealed record Rules([property: JsonRequired] string Product,
    [property: JsonRequired] decimal UnitPrice);
