using Arb.Core.Tests.TestSupport;

namespace Arb.Core.Tests.Regression;

/// <summary>
/// B-02 (S4, builder recheck before closing Tahap 1). TEAM_WORKFLOW §5 rule 5: API keys never enter the repo. From Tahap 2
/// on, adapters read keys from env files / local config on the VPS; .gitignore excluded none of the usual secret files, so
/// one `git add -A` on the VPS could commit them (GitGuardian on PRs would flag it only after the push). Guard: the
/// patterns stay in .gitignore (behaviour checked with `git check-ignore` in REGRESSION_LOG).
/// </summary>
public class B02_SecretFilesIgnoredTests
{
    private static readonly string[] Required =
    {
        ".env",
        ".env.*",
        "*.pem",
        "*.key",
        "*.p12",
        "*.pfx",
        "secrets*.json",
        "*.secrets.json",
        "appsettings.*.local.json",
    };

    [Fact]
    public void SecretFilePatterns_AreIgnored()
    {
        var lines = new HashSet<string>(
            RepoFiles.ReadAll(".gitignore").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')),
            StringComparer.Ordinal);
        string[] missing = Required.Where(p => !lines.Contains(p)).ToArray();
        Assert.True(missing.Length == 0, ".gitignore misses: " + string.Join(", ", missing));
    }
}
