using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntegrityGuard;

if (args.Length == 2 && args[0] == "keygen")
{
    Directory.CreateDirectory(args[1]);
    var privatePath = Path.Combine(args[1], "private.pem");
    var publicPath = Path.Combine(args[1], "public.pem");
    if (File.Exists(privatePath) || File.Exists(publicPath))
        throw new IOException("Refusing to overwrite existing keys.");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);
    File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem(), Encoding.ASCII);
    Console.WriteLine("Development keys created. Never distribute private.pem.");
}
else if (args.Length >= 5 && args[0] == "sign")
{
    if (!long.TryParse(args[3], out var release) || release < 1)
        throw new ArgumentException("Release must be a positive integer.");
    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(args[1]));
    var root = Path.GetFullPath(args[2]);
    var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in args.Skip(4))
    {
        if (Path.GetFileName(name) != name || name.Contains(':'))
            throw new ArgumentException("Only filenames within the output directory are supported.");
        using var stream = File.OpenRead(Path.Combine(root, name));
        if (stream.Length > IntegrityVerifier.MaxFileBytes) throw new IOException("File too large.");
        files.Add(name, Convert.ToHexString(SHA256.HashData(stream)));
    }
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new Manifest(1, files, release));
    var signature = key.SignData(bytes, HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    File.WriteAllBytes(Path.Combine(root, "integrity.json"), bytes);
    File.WriteAllBytes(Path.Combine(root, "integrity.sig"), signature);
    var result = IntegrityVerifier.Verify(root, key.ExportSubjectPublicKeyInfoPem(), files.Keys.ToArray(), release);
    if (!result.Success) throw new InvalidOperationException($"Signed output failed verification: {result.Code}");
    Console.WriteLine($"Signed and verified {files.Count} files.");
}
else
{
    Console.Error.WriteLine("keygen <directory> | sign <private.pem> <output-directory> <release> <file> ...");
    return 2;
}
return 0;
