namespace Zapret.Core;

public sealed record BundleLayout(string Catalogs, string Binaries, string SeedLists, string Manifest)
{
    public static BundleLayout Resolve(string directory)
    {
        string root = Path.GetFullPath(directory);
        // Prefer a complete installed/portable layout if both layouts coexist.
        bool repository = !File.Exists(Path.Combine(root, "engine-manifest.json")) &&
            (Directory.Exists(Path.Combine(root, "resources", "catalogs")) || Directory.Exists(Path.Combine(root, "vendor", "zapret")));
        return repository
            ? new(Path.Combine(root, "resources", "catalogs"), Path.Combine(root, "vendor", "zapret", "bin"),
                  Path.Combine(root, "resources", "lists"), Path.Combine(root, "vendor", "zapret", "engine-manifest.json"))
            : new(root, Path.Combine(root, "bin"), Path.Combine(root, "lists"), Path.Combine(root, "engine-manifest.json"));
    }
}
