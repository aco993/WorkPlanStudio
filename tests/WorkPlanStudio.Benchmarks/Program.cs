using BenchmarkDotNet.Running;

// `dotnet run -c Release --project tests/WorkPlanStudio.Benchmarks -- --filter '*'`
// Add `--job short` for a quick pass (what the performance workflow runs).
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

public partial class Program;
