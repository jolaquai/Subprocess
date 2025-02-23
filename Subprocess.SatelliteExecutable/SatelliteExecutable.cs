using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Emit;

using Subprocess.Core;

namespace Subprocess.SatelliteExecutable;

internal class SatelliteExecutable
{
    public static async Task<int> Main(string[] args)
    {
        var dataDir = args[0];
        Console.WriteLine($"Data path: '{dataDir}'");

        byte[] il;
        await using (var ilStream = File.OpenRead(Path.Combine(dataDir, "il.bin")))
        {
            il = MessagePackUtil.Deserialize<byte[]>(ilStream);
        }

        Console.WriteLine("Recompiling dynamic method");
        var method = new DynamicMethod("dynamicSubprocessWork", typeof(Task<int>),
            [typeof(BlockingCollection<string>), typeof(object[]), typeof(CancellationToken)]);
        method.GetDynamicILInfo().SetCode(il, 24);
        var work = method.CreateDelegate<SubprocessWork>();

        var pipeName = File.ReadAllText(Path.Combine(dataDir, "pipeName.str")).Trim();
        Console.WriteLine($"Connecting to pipe '{pipeName}'");
        using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var reader = new StreamReader(pipeClient, leaveOpen: true);
        using var writer = new StreamWriter(pipeClient, leaveOpen: true);
        Console.WriteLine("Beginning connection initialization");
        var connectionInit = Task.Run(async () =>
        {
            await pipeClient.ConnectAsync();
            Console.WriteLine("Connected to pipe");

            Console.WriteLine("Awaiting sync");
            string temp;
            do
            {
                temp = await reader.ReadLineAsync();
            }
            while (temp != "{SYNC}");
        });

        object[] arguments = null;
        await Task.WhenAll(
            Task.Run(async () =>
            {
                var argsPath = Path.Combine(dataDir, "args.bin");
                if (!File.Exists(argsPath))
                {
                    return;
                }

                Console.WriteLine("Deserializing arguments");
                await using (var argsStream = File.OpenRead(argsPath))
                {
                    arguments = MessagePackUtil.Deserialize<object[]>(argsStream);
                }
            }),
            Task.Run(async () =>
            {
                var packagesPath = Path.Combine(dataDir, "packages.zip");
                if (!File.Exists(packagesPath))
                {
                    return;
                }

                await using (var packagesStream = File.OpenRead(packagesPath))
                using (var archive = new ZipArchive(packagesStream, ZipArchiveMode.Read, leaveOpen: true))
                {
                    Console.WriteLine("Loading NuGet packages");
                    foreach (var nupkg in archive.Entries)
                    {
                        // subarchive
                        using var nupkgStream = nupkg.Open();
                        using var nupkgArchive = new ZipArchive(nupkgStream, ZipArchiveMode.Read, leaveOpen: true);
                        foreach (var entry in nupkgArchive.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                        {
                            await using var ms = new MemoryStream();
                            await using (var entryStream = entry.Open())
                            {
                                await entryStream.CopyToAsync(ms);
                            }

                            _ = Assembly.Load(ms.ToArray());
                        }
                    }
                }
            }),
            Task.Run(async () =>
            {
                var assembliesPath = Path.Combine(dataDir, "asm.zip");
                if (!File.Exists(assembliesPath))
                {
                    return;
                }

                await using (var asmStream = File.OpenRead(assembliesPath))
                using (var archive = new ZipArchive(asmStream, ZipArchiveMode.Read, leaveOpen: true))
                {
                    Console.WriteLine("Loading supplied assemblies");
                    foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        await using var ms = new MemoryStream();
                        await using (var entryStream = entry.Open())
                        {
                            await entryStream.CopyToAsync(ms);
                        }

                        _ = Assembly.Load(ms.ToArray());
                    }
                }
            })
        );

        Console.WriteLine("Awaiting connection initialization... ");
        await connectionInit;
        Console.WriteLine("Connection initialized");

        Console.WriteLine("Setting up background workers (message sink, co-op cancellation support)");
        var messages = new BlockingCollection<string>();
        var cts = new CancellationTokenSource();
        var pipeDrain = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    switch (line)
                    {
                        // Swallow further syncs
                        case "{SYNC}":
                            continue;
                        // Cancellation signals go to the caller
                        case "{END}":
                            cts.Cancel();
                            return;
                        // Add more cases here as needed to handle system-level messages
                        default:
                            messages.Add(line); // Pass on to the caller's work
                            continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        Console.WriteLine("Calling work function");
        var result = await work(messages, arguments, cts.Token);

        Console.WriteLine("Finished, cleaning up");
        // Since there is nothing left to respond to an end signal, do some clean-up here, then exit
        cts.Cancel();
        Console.WriteLine("Awaiting pipe drain");
        await pipeDrain;

        Console.ReadLine();

        return result;
    }
}

public static class Console
{
    public static void WriteLine(object o) => System.Console.WriteLine($"[{DateTime.Now}] {o}");
    public static string ReadLine() => System.Console.ReadLine();
}