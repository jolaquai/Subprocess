using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Subprocess.Core;

namespace Subprocess;

/// <summary>
/// Represents a <see cref="Task"/>-like object that offloads work into a separate process.
/// It allows one-way communication from the calling process to the instance.
/// </summary>
public class Subprocess
{
    // While the design of this class is inspired by Task, there are obviously numerous drawbacks.
    // The biggest one is that delegates cannot be marshalled into a separate process, meaning loading the
    // calling assembly into the process is necessary. This means performance will be rather poor.
    // Additionally, methods must be static and need their parameters to be serializable.

    private readonly Process _process;
    // _task is not exposed publicly since Subprocess itself is the thing consumers should await
    private Task<int> _task;
    private readonly NamedPipeServerStream _pipe;

    /// <summary>
    /// Gets the <see cref="SubprocessWork"/> delegate that represents the work to be done in the subprocess.
    /// </summary>
    public SubprocessWork Work { get; }
    /// <summary>
    /// Gets a dictionary that maps from process exit codes to human-readable descriptions.
    /// If the subprocess exits and returns a code that is not equal to <see cref="SuccessExitCode"/>, an exception is thrown using the exit code and the provided description.
    /// </summary>
    public IReadOnlyDictionary<int, string> ExitCodes { get; }
    /// <summary>
    /// The exit code returned by the subprocess that indicates success.
    /// </summary>
    public int SuccessExitCode { get; }
    /// <summary>
    /// Gets a <see cref="SubprocessStatus"/> value that indicates the current status of the subprocess.
    /// </summary>
    public SubprocessStatus Status => _task.Status switch
    {
        TaskStatus.Created or TaskStatus.WaitingForActivation or TaskStatus.WaitingToRun => SubprocessStatus.Created,
        TaskStatus.Running or TaskStatus.WaitingForChildrenToComplete => SubprocessStatus.Running,
        TaskStatus.RanToCompletion => SubprocessStatus.RanToCompletion,
        TaskStatus.Canceled => SubprocessStatus.Canceled,
        TaskStatus.Faulted => SubprocessStatus.Faulted,
        _ => throw new InvalidOperationException("The subprocess is in an invalid state.")
    };

    /// <summary>
    /// Gets a <see cref="StreamReader"/> that can be used to read messages from the subprocess.
    /// </summary>
    public StreamReader Reader { get; private set; }
    /// <summary>
    /// Gets a <see cref="StreamWriter"/> that can be used to send messages to the subprocess.
    /// Single-word messages enclosed in braces are reserved for system-level messages. Usage of these messages is discouraged and may lead to unexpected behavior.
    /// </summary>
    public StreamWriter Writer { get; private set; }

    #region await support
    /// <summary>
    /// Gets an awaiter for this <see cref="Subprocess"/> instance.
    /// </summary>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubprocessAwaiter GetAwaiter() => new SubprocessAwaiter(this);
    /// <summary>
    /// Gets a configured awaiter for this <see cref="Subprocess"/> instance.
    /// </summary>
    /// <param name="continueOnCapturedContext">Whether to marshal the continuation back to the original context captured.</param>
    /// <returns>The configured awaiter.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubprocessAwaiter ConfigureAwait(bool continueOnCapturedContext) => new SubprocessAwaiter(this, continueOnCapturedContext ? ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.None);
    /// <summary>
    /// Gets a configured awaiter for this <see cref="Subprocess"/> instance.
    /// </summary>
    /// <param name="options">The options to use for the awaiter.</param>
    /// <returns>The configured awaiter.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SubprocessAwaiter ConfigureAwait(ConfigureAwaitOptions options) => new SubprocessAwaiter(this, options);
    #endregion

    private static readonly HttpClient _nugetClient = new HttpClient()
    {
        BaseAddress = new Uri("https://api.nuget.org/v3-flatcontainer/"),
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = int.MaxValue
    };
    private static readonly Exception _unusable;
    private static readonly Mutex _mutex = new Mutex(false, "Subprocess.Mutex");
    private static readonly string _tempPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Subprocess", "SatelliteExecutable.exe");
    private static void EnsureUsable()
    {
        if (_unusable is not null)
        {
            throw new NotSupportedException("The Subprocess class is not usable due to an initialization error. Refer to the inner exception for more details.", _unusable);
        }
    }

    const string resName = "SatelliteExecutable";
    static Subprocess()
    {
        using var asm = typeof(Subprocess).Assembly.GetManifestResourceStream(resName);

        try
        {
            _ = _mutex.WaitOne();

            // Initialize by unpacking the prepared worker executable from our own resources
            Directory.CreateDirectory(Path.GetDirectoryName(_tempPath));
            using (var file = File.Create(_tempPath))
            {
                asm.CopyTo(file);
            }
        }
        catch (Exception ex) // Prevent throwing a TypeInitializationException, we'll pack that into EnsureUsable later
        {
            _unusable = ex;
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Creates a new <see cref="Subprocess"/> instance that executes the provided IL in a separate process.
    /// </summary>
    /// <param name="dataPath">The path to the directory containing the data for the subprocess. Should be provided by <see cref="CreateAsync(MethodInfo, object[], ValueTuple{string, Version, bool}[], string[])"/>.</param>
    /// <param name="pipeName">The name of the pipe to use for communication with the subprocess. Should be provided by <see cref="CreateAsync(MethodInfo, object[], ValueTuple{string, Version, bool}[], string[])"/>.</param>
    internal Subprocess(string dataPath, string pipeName)
    {
        EnsureUsable();

        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Writer = new StreamWriter(_pipe, leaveOpen: true);
        Reader = new StreamReader(_pipe, leaveOpen: true);

        _process = new Process()
        {
            StartInfo = new ProcessStartInfo()
            {
                FileName = _tempPath,
                ArgumentList =
                {
                    dataPath
                },
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false,
                CreateNoWindow = false
            }
        };
    }

    /// <summary>
    /// Starts the subprocess and synchronizes with it.
    /// </summary>
    /// <returns>A <see cref="Task"/> that represents the synchronization procedure.</returns>
    public async Task StartAsync()
    {
        _process.Start();

        await _pipe.WaitForConnectionAsync();
        Writer.WriteLine("{SYNC}");
        Writer.Flush();
        string temp;
        do
        {
            temp = await Reader.ReadLineAsync();
        }
        while (temp != "{SYNC}");

        var tcs = new TaskCompletionSource<int>();
        _process.Exited += (_, _) => tcs.SetResult(_process.ExitCode);
        _task = tcs.Task;
    }
    /// <summary>
    /// Sends a message to the subprocess, indicating that it should exit as soon as possible.
    /// </summary>
    /// <returns>A <see cref="Task"/> that represents the completion of the request. Be cautious when <see langword="await"/>ing this <see cref="Task"/> since the request may not be respected.</returns>
    public Task RequestExitAsync()
    {
        Writer.WriteLine("{END}");
        Writer.Flush();
        return _process.WaitForExitAsync();
    }
    /// <summary>
    /// Forcibly terminates the subprocess.
    /// </summary>
    /// <returns>A <see cref="Task"/> that represents the completion of the termination.</returns>
    public Task TerminateAsync()
    {
        _process.Kill();
        return _process.WaitForExitAsync();
    }

    /// <summary>
    /// Starts a new subprocess that executes the provided method with the provided arguments.
    /// Note that this method being asynchronous is required to await synchronization with the subprocess.
    /// </summary>
    /// <param name="method">The method to execute in the subprocess. It must be static and have no external references, otherwise it cannot be rewritten into an executable method.</param>
    /// <param name="arguments">The arguments to pass to the method.</param>
    /// <returns>A <see cref="Subprocess"/> instance that represents the subprocess.</returns>
    /// <exception cref="ArgumentException">Thrown when the provided method is not static, does not have a body, or could not be converted into a <see cref="SubprocessWork"/> delegate.</exception>
    public static async Task<Subprocess> RunAsync(MethodInfo method, object[] arguments)
    {
        var sp = await CreateAsync(method, arguments);
        await sp.StartAsync();
        return sp;
    }
    /// <summary>
    /// Creates a new <see cref="Subprocess"/> instance that executes the provided method with the provided arguments.
    /// </summary>
    /// <param name="method">The method to execute in the subprocess. It must be static.</param>
    /// <param name="arguments">The arguments to pass to the method.</param>
    /// <param name="packages">NuGet packages to download, unpack and load into the host assembly of the subprocess.</param>
    /// <param name="assemblies">Paths to DLLs to load into the host assembly of the subprocess.</param>
    /// <returns>A <see cref="Subprocess"/> instance that represents the subprocess.</returns>
    /// <exception cref="ArgumentException">Thrown when the provided method is not static, does not have a body, or could not be converted into a <see cref="SubprocessWork"/> delegate.</exception>
    public static async Task<Subprocess> CreateAsync(MethodInfo method, object[] arguments, (string, Version, bool)[] packages = null, string[] assemblies = null)
    {
        if (!method.IsStatic)
        {
            throw new ArgumentException("The method to execute must be static, otherwise the IL cannot be rewritten into an executable method.", nameof(method));
        }

        SubprocessWork delg;
        try
        {
            delg = method.CreateDelegate<SubprocessWork>();
        }
        catch (Exception ex)
        {
            throw new ArgumentException("The provided method could not be converted into a SubprocessWork delegate. Make sure it matches the delegate type's signature.", nameof(method), ex);
        }

        var opId = Guid.NewGuid().ToString();
        var dataPath = Path.Combine(Path.GetTempPath(), opId);
        Directory.CreateDirectory(dataPath);

        var body = method.GetMethodBody() ?? throw new ArgumentException("The provided method does not have a body.", nameof(method));
        var bodyArr = body.GetILAsByteArray();
        Debug.Assert(bodyArr?.Length is > 0);

        var pipeName = opId;
        // Scope the streams away
        {
            var ilStream = File.Create(Path.Combine(dataPath, "il.bin"));
            await using (ilStream.ConfigureAwait(false))
            {
                MessagePackUtil.Serialize(bodyArr, ilStream);
            }
            var pipeNameStream = File.Create(Path.Combine(dataPath, "pipeName.str"));
            var writer = new StreamWriter(pipeNameStream, leaveOpen: true);
            await using (pipeNameStream.ConfigureAwait(false))
            await using (writer.ConfigureAwait(false))
            {
                writer.Write(pipeName);
            }
        }

        if (arguments?.Length is > 0)
        {
            var argsStream = File.Create(Path.Combine(dataPath, "args.bin"));
            await using (argsStream.ConfigureAwait(false))
            {
                MessagePackUtil.Serialize(arguments, argsStream);
            }
        }

        if (packages?.Length is > 0)
        {
            await using (var packagesZip = File.Create(Path.Combine(dataPath, "packages.zip")))
            using (var archive = new ZipArchive(packagesZip, ZipArchiveMode.Update, leaveOpen: true))
            {
                for (var i = 0; i < packages.Length; i++)
                {
                    var (id, ver, allowGreater) = packages[i];
                    id = id.ToLowerInvariant();

                    if (allowGreater)
                    {
                        var versionsResponse = await _nugetClient.GetStringAsync($"{id}/index.json").ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(versionsResponse);
                        ver = doc.RootElement.GetProperty("versions")
                            .EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(v => !v.Contains('-')) // disallow prerelease versions
                            .Max(v => new Version(v));
                    }

                    var entry = archive.CreateEntry($"{id}.{ver:3}.nupkg");
                    await using (var package = await _nugetClient.GetStreamAsync($"{id}/{id}.{ver:3}.nupkg").ConfigureAwait(false))
                    await using (var entryStream = entry.Open())
                    {
                        await package.CopyToAsync(entryStream).ConfigureAwait(false);
                    }
                }
            }
        }

        if (assemblies?.Length is > 0)
        {
            await using (var asmZip = File.Create(Path.Combine(dataPath, "asm.zip")))
            using (var archive = new ZipArchive(asmZip, ZipArchiveMode.Update, leaveOpen: true))
            {
                for (var i = 0; i < assemblies.Length; i++)
                {
                    var path = assemblies[i];

                    var entry = archive.CreateEntry(Path.GetFileName(path));
                    await using (var package = await _nugetClient.GetStreamAsync(path).ConfigureAwait(false))
                    await using (var entryStream = entry.Open())
                    {
                        await package.CopyToAsync(entryStream).ConfigureAwait(false);
                    }
                }
            }
        }

        return new Subprocess(dataPath, pipeName);
    }

    /// <summary>
    /// Implements the awaitable pattern to enable waiting for the completion of a <see cref="Subprocess"/>.
    /// </summary>
    public readonly struct SubprocessAwaiter : INotifyCompletion, ICriticalNotifyCompletion
    {
        private readonly Subprocess _subprocess;
        private readonly ConfiguredTaskAwaitable<int>.ConfiguredTaskAwaiter _awaiter;

        internal SubprocessAwaiter(Subprocess subprocess, ConfigureAwaitOptions options = ConfigureAwaitOptions.None)
        {
            _subprocess = subprocess;
            _awaiter = _subprocess._task.ConfigureAwait(options).GetAwaiter();
        }

        /// <summary>
        /// Gets a value indicating whether the <see cref="Subprocess"/> has finished execution (that is, whether its associated <see cref="Process"/> has exited).
        /// </summary>
        public readonly bool IsCompleted => _subprocess._task.IsCompleted;
        /// <summary>
        /// Gets a value indicating whether the <see cref="Subprocess"/> has finished execution successfully (that is, whether its associated <see cref="Process"/> has exited with an exit code equal to its <see cref="SuccessExitCode"/>).
        /// </summary>
        public readonly bool IsCompletedSuccessfully => _subprocess._task.IsCompletedSuccessfully;

        /// <summary>
        /// Schedules the continuation action that's invoked when the <see cref="Subprocess"/> completes.
        /// </summary>
        /// <param name="continuation">The action to invoke when the operation completes.</param>
        public readonly void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
        /// <inheritdoc cref="OnCompleted(Action)"/>"
        public readonly void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
        /// <summary>
        /// Ends the wait for the completion of the <see cref="Subprocess"/>.
        /// </summary>
        /// <returns>The exit code of the subprocess.</returns>
        public int GetResult() => _awaiter.GetResult();
    }
}

/// <summary>
/// Specifies the execution status of a <see cref="Subprocess"/>.
/// </summary>
public enum SubprocessStatus
{
    /// <summary>
    /// Specifies that the subprocess has been created but has not yet started.
    /// </summary>
    Created,
    /// <summary>
    /// Specifies that the subprocess is running.
    /// </summary>
    Running,
    /// <summary>
    /// Specifies that the subprocess has completed successfully.
    /// </summary>
    RanToCompletion,
    /// <summary>
    /// Specifies that the subprocess has been canceled.
    /// </summary>
    Canceled,
    /// <summary>
    /// Specifies that the subprocess has been terminated forcefully (that is, not through cooperative cancellation, but by terminating the process).
    /// </summary>
    ForceTerminated,
    /// <summary>
    /// Specifies that the subprocess has faulted (that is, exited with an exit code unequal to its <see cref="ISubprocess.SuccessExitCode"/>).
    /// </summary>
    Faulted,
}
