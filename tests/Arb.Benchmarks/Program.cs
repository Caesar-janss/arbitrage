using BenchmarkDotNet.Running;

namespace Arb.Benchmarks;

/// <summary>
/// dotnet run -c Release --project tests/Arb.Benchmarks -- --filter '*'
/// Acceptance: the "Allocated" column must read "-" (0 B) for every benchmark.
/// </summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
