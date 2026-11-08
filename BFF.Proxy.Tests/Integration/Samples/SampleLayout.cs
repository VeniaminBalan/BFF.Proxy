namespace BFF.Proxy.Tests.Integration.Samples;

// Recreates what a sample's Dockerfile layers onto the base image's /app (the BFF's content root), using the
// sample's real files, so the tests break when a sample stops matching the BFF's behaviour.
public sealed class SampleLayout : IDisposable
{
    public static readonly string RepoRoot = FindRepoRoot();
    public static string SamplesDir => Path.Combine(RepoRoot, "samples");

    private SampleLayout()
    {
        ContentRoot = Path.Combine(Path.GetTempPath(), "bff-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRoot);
    }

    public string ContentRoot { get; }

    // An empty /app, like the published base image without a wwwroot.
    public static SampleLayout Create() => new();

    // Dockerfile `COPY <samples/sourceDir>/ ./<targetDir>/`
    public SampleLayout CopyDirectory(string sampleSourceDir, string targetDir)
    {
        var source = Path.Combine(SamplesDir, sampleSourceDir);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Sample directory not found: {source}");

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(ContentRoot, targetDir, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return this;
    }

    // Dockerfile `COPY <file> ./<targetFile>`; sourcePath is relative to the repo root.
    public SampleLayout CopyFile(string repoRelativeSource, string targetFile)
    {
        File.Copy(Path.Combine(RepoRoot, repoRelativeSource), Path.Combine(ContentRoot, targetFile));
        return this;
    }

    public SampleLayout WriteFile(string targetFile, string content)
    {
        var target = Path.Combine(ContentRoot, targetFile);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        return this;
    }

    public static string ReadSampleFile(string sampleRelativePath) =>
        File.ReadAllText(Path.Combine(SamplesDir, sampleRelativePath));

    public void Dispose()
    {
        try
        {
            Directory.Delete(ContentRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a file still open on Windows. It's in the temp folder.
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BFF.Proxy.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (BFF.Proxy.slnx) not found above the test output directory.");
    }
}
