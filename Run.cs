using System;
using System.Diagnostics;
class Program {
    static void Main() {
        var proc = Process.Start(new ProcessStartInfo {
            FileName = "dotnet",
            Arguments = "build tests/LifeDrop.Database.Tests/LifeDrop.Database.Tests.csproj --no-restore",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        proc.WaitForExit();
        Console.WriteLine("Exit: " + proc.ExitCode);
    }
}
