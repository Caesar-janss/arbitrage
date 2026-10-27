namespace Arb.Core.Tests.TestSupport;

/// <summary>Locates repo files (SQL migrations) from the test output directory; ARB_REPO_ROOT overrides.</summary>
internal static class RepoFiles
{
    public static string Find(string relativePath)
    {
        string? root = Environment.GetEnvironmentVariable("ARB_REPO_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            string p = Path.Combine(root, relativePath);
            if (File.Exists(p))
            {
                return p;
            }
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string p = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(p))
            {
                return p;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"'{relativePath}' not found above {AppContext.BaseDirectory} (set ARB_REPO_ROOT)");
    }

    public static string ReadAll(string relativePath) => File.ReadAllText(Find(relativePath));

    /// <summary>All migration files in apply order (V001, V002, …), as db/tests/run_tests.sh applies them.</summary>
    public static string[] Migrations()
    {
        string dir = Path.GetDirectoryName(Find("db/migrations/V001__schemas_reference.sql"))!;
        string[] files = Directory.GetFiles(dir, "V*.sql");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }
}
