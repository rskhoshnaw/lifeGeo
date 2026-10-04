using System;
using Microsoft.Build.Logging.StructuredLogger;

class Program
{
    static void Main(string[] args)
    {
        var build = Serialization.Read("build.binlog");
        Console.WriteLine("Succeeded: " + build.Succeeded);
    }
}
