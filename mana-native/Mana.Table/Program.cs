using Mana.Forge;
using Mana.Client;
using Mana.Contracts;
using System.Text.Json;

namespace Mana.Table;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string? Arg(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        var profile = Arg("--profile") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManaTableNative");
        Directory.CreateDirectory(profile);
        try {
            Microsoft.Xna.Framework.Point? size = null;
            if (Arg("--size")?.Split('x') is { Length: 2 } dimensions && int.TryParse(dimensions[0], out int width) && int.TryParse(dimensions[1], out int height)) size = new(Math.Clamp(width, 800, 3840), Math.Clamp(height, 450, 2160));
            var bundle = Arg("--engine-resources") ?? Path.Combine(AppContext.BaseDirectory, "engine");
            if (!Directory.Exists(bundle)) throw new IOException("Missing bundled engine. Run the native packaging script first, or pass --engine-resources pointing to the package's resources directory.");
            var backend = Arg("--engine") ?? "forge";
            if (backend != "forge") throw new NotSupportedException($"Engine '{backend}' is not installed. Available: forge.");
            var paths = new ForgePaths(Path.Combine(bundle, "runtime", "bin", "java.exe"), Path.Combine(bundle, "forge-engine.jar"), Path.Combine(bundle, "forge-res"), profile);
            var engine = new ForgeEngine(paths);
            try {
                ICardArtSource? art = null;
                if (Arg("--art-manifest") is { } manifestPath) {
                    string manifest = Path.GetFullPath(manifestPath);
                    var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest)) ?? throw new IOException("Invalid art manifest");
                    art = new LocalArtSource(Path.GetDirectoryName(manifest)!, entries);
                }
                using var game = new TableGame(engine, profile, Arg("--capture"), Arg("--fixture"), size, artSource: art);
                game.Run();
            } finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return 0;
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(profile, "native-error.log"), ex.ToString());
            if (!args.Contains("--capture")) MessageBox.Show(ex.Message, "Mana Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
