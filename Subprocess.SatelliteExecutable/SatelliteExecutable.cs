using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection.Emit;

using Subprocess.Core;

namespace Subprocess.SatelliteExecutable;

internal class SatelliteExecutable
{
    public static async Task<int> Main(string[] args)
    {
        var il = MessagePackUtil.Deserialize<byte[]>(Convert.FromBase64String(args[0]));
        var method = new DynamicMethod("dynamicSubprocessWork", typeof(Task<int>), [typeof(BlockingCollection<string>), typeof(object[]), typeof(CancellationToken)]);
        method.GetDynamicILInfo().SetCode(il, 24);
        var work = method.CreateDelegate<SubprocessWork>();

        var pipeName = args[1];
        using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var reader = new StreamReader(pipeClient, leaveOpen: true);
        using var writer = new StreamWriter(pipeClient, leaveOpen: true);
        var connectionInit = Task.WhenAll(
            pipeClient.ConnectAsync(),
            Task.Run(async () =>
            {
                string temp;
                do
                {
                    temp = await reader.ReadLineAsync();
                }
                while (temp != "{SYNC}");
            })
        );

        var arguments = MessagePackUtil.Deserialize<object[]>(Convert.FromBase64String(args[2]));
        await connectionInit;

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

        var result = await work(messages, arguments, cts.Token);
        // Since there is nothing left to respond to an end signal, do some clean-up here, then exit
        cts.Cancel();
        await pipeDrain;
        return result;
    }
}
