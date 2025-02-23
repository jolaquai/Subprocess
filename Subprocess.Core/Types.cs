using System.Collections.Concurrent;

namespace Subprocess.Core;

/// <summary>
/// Represents a method that can be executed in a subprocess.
/// </summary>
/// <param name="messages">A <see cref="BlockingCollection{T}"/> the calling process can use to send messages to the subprocess.</param>
/// <param name="args">Arbitrary data to pass to the subprocess as command-line arguments. May be <see langword="null"/>.</param>
/// <param name="cancellationToken">A <see cref="CancellationToken"/> that the method should monitor for cancellation requests.</param>
/// <returns>A <see cref="Task{TResult}"/> that represents the work to be done in the subprocess. It resolves to the exit code of the subprocess.</returns>
public delegate Task<int> SubprocessWork(BlockingCollection<string> messages, object[] args, CancellationToken cancellationToken);
