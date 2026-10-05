using System.Reflection;
using System.Text.Json;
using IntegrityGuard;

namespace DemoApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DemoApp.public.pem")
            ?? throw new InvalidOperationException("Missing embedded verification key.");
        using var reader = new StreamReader(stream);
        var key = reader.ReadToEnd();
        if (args.Contains("--verify"))
        {
            Environment.ExitCode = Verify(key).Success ? 0 : 1;
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new DemoWindow(key));
    }

    internal static IntegrityPolicy Policy(string key) => new(
        AppContext.BaseDirectory, key,
        ["DemoApp.exe", "DemoApp.dll", "DemoApp.deps.json", "DemoApp.runtimeconfig.json",
         "IntegrityGuard.dll", "demo-rules.json"]);

    internal static VerificationResult Verify(string key) =>
        new IntegritySession(Policy(key)).Check(IntegrityCheckpoint.Startup);
}

internal sealed class DemoWindow : Form
{
    private readonly Label status = new() { Name = "IntegrityStatus", AutoSize = true, MaximumSize = new Size(640, 0) };
    private readonly Button action = new() { Name = "CalculateQuote", Text = "Demo teklif hesapla", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly IntegritySession session;
    private bool busy;
    private bool blocked;

    internal DemoWindow(string key)
    {
        session = new IntegritySession(Program.Policy(key));
        Text = "IntegrityGuard - Dosya butunlugu demosu";
        ClientSize = new Size(720, 310);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(24), WrapContents = false };
        panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(640, 0), Text =
            "Imzali dosya butunlugu kontrolu. Bellek icindeki degisiklikleri veya Frida'yi tespit etmez.\n" +
            "Dogrulama basarisizsa bu oturumda demo islemi engellenir." });
        panel.Controls.Add(status);
        panel.Controls.Add(action);
        Controls.Add(panel);
        Shown += async (_, _) => { await Check(false, IntegrityCheckpoint.Startup); if (!IsDisposed) timer.Start(); };
        timer.Tick += async (_, _) => await Check(false, IntegrityCheckpoint.Periodic);
        action.Click += async (_, _) => await Check(true, IntegrityCheckpoint.BeforeOperation);
        FormClosed += (_, _) => timer.Dispose();
    }

    private async Task Check(bool calculate, IntegrityCheckpoint checkpoint)
    {
        if (busy || blocked) return;
        busy = true;
        action.Enabled = false;
        try
        {
            var result = await Task.Run(() => session.Check(checkpoint));
            if (IsDisposed) return;
            if (!result.Success)
            {
                blocked = true;
                status.Text = $"ENGELLENDI: {result.Code}. Dosyalari guvenilir dagitimdan geri yukleyip yeniden baslatin.";
                return;
            }
            // Consume the authenticated bytes, never reopen the rules file after verification.
            var rules = JsonSerializer.Deserialize<Rules>(result.Files["demo-rules.json"]);
            if (rules is null || string.IsNullOrWhiteSpace(rules.Product) ||
                rules.UnitPrice < 0 || rules.UnitPrice > 1_000_000)
                throw new JsonException("Invalid rules.");
            status.Text = $"DOGRULANDI - {DateTime.Now:T}";
            if (calculate) MessageBox.Show(this, $"{rules.Product}: {rules.UnitPrice:N2} TL", "Demo teklif");
        }
        catch (JsonException)
        {
            if (!IsDisposed) { blocked = true; status.Text = "ENGELLENDI: Gecersiz is kurallari."; }
        }
        finally
        {
            busy = false;
            if (!IsDisposed) action.Enabled = !blocked;
        }
    }

    private sealed record Rules(string Product, decimal UnitPrice);
}
