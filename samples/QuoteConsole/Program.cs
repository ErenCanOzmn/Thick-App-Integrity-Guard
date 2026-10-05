using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CustomerIntegration;

// Interactive example: pipe commands or type 'quote 2', 'check', 'exit'.
// Output is JSON Lines so real process behavior can be tested independently.
using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("QuoteConsole.public.pem")
    ?? throw new InvalidOperationException("Missing embedded public key.");
using var reader = new StreamReader(resource);
var rules = new ProtectedRules(AppContext.BaseDirectory, reader.ReadToEnd(), minimumRelease: 1);
var startup = rules.CheckStartup();
Emit("startup", startup.Success, startup.Code);
if (!startup.Success) return 1;
if (!Console.IsInputRedirected)
    Console.Error.WriteLine("Commands: quote <quantity>, check, exit. Signed local data demo; not server authorization.");

string? line;
while ((line = Console.ReadLine()) is not null)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 1 && parts[0] == "exit") return 0;
    if (parts.Length == 1 && parts[0] == "check")
    {
        var check = rules.CheckPeriodic();
        Emit("check", check.Success, check.Code);
    }
    else if (parts.Length == 2 && parts[0] == "quote" &&
        int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) &&
        quantity is >= 1 and <= 10000)
    {
        if (!rules.TryReadForOperation(out var value, out var code)) Emit("quote", false, code);
        else Console.WriteLine(JsonSerializer.Serialize(new { operation = "quote", allowed = true,
            code, product = value!.Product, quantity, total = value.UnitPrice * quantity }));
    }
    else Emit("command", false, "InvalidCommand");
}
return 0;

static void Emit(string operation, bool allowed, string code) =>
    Console.WriteLine(JsonSerializer.Serialize(new { operation, allowed, code }));
