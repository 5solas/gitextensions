using GitExtensions.Extensibility;

namespace GitCommands.Git.Broker;

/// <summary>
///  A process which has already exited successfully with a known output.
/// </summary>
internal sealed class CannedProcess(byte[] output) : IProcess
{
    public StreamWriter StandardInput { get; } = new(new MemoryStream());

    public StreamReader StandardOutput { get; } = new(new MemoryStream(output, writable: false));

    public string StandardError => "";

    public void Dispose()
    {
    }

    public void Kill(bool entireProcessTree = false)
    {
    }

    public int WaitForExit() => 0;

    public Task<int> WaitForExitAsync() => Task.FromResult(0);

    public Task<int> WaitForExitAsync(CancellationToken token) => Task.FromResult(0);

    public void WaitForInputIdle()
    {
    }
}
