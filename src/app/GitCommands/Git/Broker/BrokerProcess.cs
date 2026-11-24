using System.ComponentModel;
using System.Diagnostics;
using GitExtensions.Extensibility;

namespace GitCommands.Git.Broker;

/// <summary>
///  A process which answers requests until it is stopped.
/// </summary>
/// <remarks>
///  It is never waited for the process: it ends when its standard input is closed (also if the application has crashed),
///  otherwise it is killed in the background.
/// </remarks>
internal sealed class BrokerProcess
{
    private static readonly TimeSpan _exitTimeout = TimeSpan.FromSeconds(2);

    private readonly IProcess _process;
    private readonly Task _exitTask;
    private int _isStopped;

    public BrokerProcess(IProcess process)
    {
        _process = process;
        Input = process.StandardInput.BaseStream;
        Output = process.StandardOutput.BaseStream;
        GitBrokerStats.CountStart();
        _exitTask = ObserveExitAsync();
    }

    /// <summary>
    ///  Gets the stream for the requests, neither buffered nor encoded.
    /// </summary>
    public Stream Input { get; }

    /// <summary>
    ///  Gets the stream of the responses, neither buffered nor decoded.
    /// </summary>
    public Stream Output { get; }

    public bool IsStopped => Volatile.Read(ref _isStopped) != 0;

    /// <summary>
    ///  Stops the process in the background. A response which has been started is completed unless <paramref name="kill"/> is set.
    /// </summary>
    public void Stop(bool kill)
    {
        if (Interlocked.Exchange(ref _isStopped, 1) != 0 && !kill)
        {
            return;
        }

        CloseInput();
        _ = Task.Run(async () =>
        {
            if (!kill)
            {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
                await Task.WhenAny(_exitTask, Task.Delay(_exitTimeout)).ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            }

            if (!_exitTask.IsCompleted)
            {
                // The process may be a launcher of the git process
                Kill(entireProcessTree: true);
            }
        });
    }

    /// <summary>
    ///  Stops the process at the end of the application, when nothing can be done in the background any longer.
    /// </summary>
    public void KillNow()
    {
        Interlocked.Exchange(ref _isStopped, 1);
        CloseInput();
        Kill(entireProcessTree: false);
    }

    private void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Trace.WriteLine(ex);
        }
    }

    private void Kill(bool entireProcessTree)
    {
        try
        {
            _process.Kill(entireProcessTree);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            // The process has exited
            Trace.WriteLine(ex);
        }
    }

    private async Task ObserveExitAsync()
    {
        // The exit may be reported while the process is locked
        await ((Task)_process.WaitForExitAsync()).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
        Interlocked.Exchange(ref _isStopped, 1);
        GitBrokerStats.CountExit();
        _process.Dispose();
    }
}
