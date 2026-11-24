using System.Text;
using GitExtensions.Extensibility;

namespace GitCommands.Git.Broker;

/// <summary>
///  A process of a repository which answers requests.
/// </summary>
/// <remarks>
///  Requests must not be sent concurrently.
///  If anything but a complete and expected response has been read (timeout, error), the session is stopped,
///  because the remaining part of the response would be taken for the next one.
/// </remarks>
internal abstract class BrokerSession : IDisposable
{
    protected const int BufferLength = 64 * 1024;

    // The start of the process can take long, e.g. because of a virus scanner
    private static readonly TimeSpan _cancelledFirstResponseTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _defaultFirstResponseTimeout = TimeSpan.FromSeconds(30);
    protected static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    // A response which is complete soon is read to its end after a cancellation, because another process start takes longer
    protected static readonly TimeSpan CancelledResponseTimeout = TimeSpan.FromMilliseconds(250);
    protected static readonly UTF8Encoding RequestEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly BrokerProcess _process;
    private readonly byte[] _buffer = new byte[BufferLength];
    private int _bufferStart;
    private int _bufferEnd;
    private TimeSpan _timeout;
    private bool _isFirstRequest = true;

    protected BrokerSession(IProcess process, TimeSpan? firstResponseTimeout)
    {
        _process = new BrokerProcess(process);
        _timeout = firstResponseTimeout ?? _defaultFirstResponseTimeout;
    }

    public bool IsStopped => _process.IsStopped;

    /// <summary>
    ///  Stops the process. Can be called on any thread.
    /// </summary>
    public void Dispose() => _process.Stop(kill: false);

    public void KillNow() => _process.KillNow();

    protected void Kill() => _process.Stop(kill: true);

    /// <summary>
    ///  Sends a request and reads its response.
    /// </summary>
    /// <exception cref="GitBrokerException">The session is stopped.</exception>
    /// <exception cref="OperationCanceledException">Also stops the session if the response cannot be completed soon.</exception>
    protected T Execute<T>(byte[] request, Func<CancellationTokenSource, T> readResponse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopped)
        {
            throw new GitBrokerException("The process has been stopped");
        }

        TimeSpan cancelledResponseTimeout = _isFirstRequest ? _cancelledFirstResponseTimeout : CancelledResponseTimeout;
        _isFirstRequest = false;
        using CancellationTokenSource timeout = new(_timeout);
        _timeout = ResponseTimeout;
        using CancellationTokenRegistration cancellation = cancellationToken.Register(() => timeout.CancelAfter(cancelledResponseTimeout));

        // Unblocks the read and write operations
        using CancellationTokenRegistration registration = timeout.Token.Register(() => _process.Stop(kill: true));
        T result;
        try
        {
            GitBrokerStats.CountRequest();
            _process.Input.Write(request);
            _process.Input.Flush();
            result = readResponse(timeout);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            _process.Stop(kill: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw ex as GitBrokerException ?? new GitBrokerException("Request failed", ex);
        }

        // The response is complete: the session remains usable
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    protected byte ReadByte()
    {
        if (_bufferStart == _bufferEnd)
        {
            _bufferStart = 0;
            _bufferEnd = _process.Output.Read(_buffer, 0, _buffer.Length);
            if (_bufferEnd == 0)
            {
                throw new GitBrokerException("The process has exited");
            }
        }

        return _buffer[_bufferStart++];
    }

    protected void ReadExactly(Span<byte> content)
    {
        int buffered = Math.Min(content.Length, _bufferEnd - _bufferStart);
        _buffer.AsSpan(_bufferStart, buffered).CopyTo(content);
        _bufferStart += buffered;
        _process.Output.ReadExactly(content[buffered..]);
    }
}
