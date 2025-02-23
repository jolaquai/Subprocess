using System.Collections.Concurrent;
using System.Reflection;

namespace TestConsole;

internal class Program
{
    static async Task Main(string[] args)
    {
        var methodInfo = typeof(Program).GetMethod("MyMethod", BindingFlags.Public | BindingFlags.Static);
        var sp = await Subprocess.Subprocess.RunAsync(methodInfo, args);
        await sp;
    }

    public static async Task<int> MyMethod(BlockingCollection<string> messages, object[] arguments, CancellationToken ct)
    {
        Console.WriteLine("Hello from subprocess!");
        Console.WriteLine("Arguments:");
        foreach (var arg in arguments)
        {
            Console.WriteLine(arg);
        }
        Console.WriteLine("I'm going to sleep for 5 seconds");
        await Task.Delay(5000, ct);
        Console.WriteLine("Goodbye from subprocess!");
        return 42;
    }
}
