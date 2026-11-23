using BenchmarkDotNet.Running;
using BenchmarkDotNetDemo.Benchmarks;
using BenchmarkDotNetDemo.Core;

// 把 Halcon 原生库所在目录加入 PATH，保证基准进程能找到 halconxl.dll。
// Core 程序集与 halcondotnetxl.dll、halconxl.dll 都位于基准输出目录。
string? halconDir = System.IO.Path.GetDirectoryName(typeof(HObjectLifecycle).Assembly.Location);
if (!string.IsNullOrEmpty(halconDir))
{
    string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    Environment.SetEnvironmentVariable(
        "PATH",
        halconDir + System.IO.Path.PathSeparator + currentPath);
}

// 常用命令：
//   dotnet run -c Release -- --list flat                列出全部基准
//   dotnet run -c Release -- --filter *                 完整跑一遍
//   dotnet run -c Release -- --filter *HandleDispose*   只跑某一组
BenchmarkSwitcher.FromAssembly(typeof(HObjectLifecycleBenchmarks).Assembly).Run(args);
