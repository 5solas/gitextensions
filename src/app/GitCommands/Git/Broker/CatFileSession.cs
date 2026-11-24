using System.Globalization;
using System.Text;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitCommands.Git.Broker;

/// <summary>
///  A <c>git cat-file --batch-command</c> process of a repository.
/// </summary>
/// <remarks>
///  Requests must not be sent concurrently.
///  If anything but a complete and expected response has been read (cancellation, timeout, error), the session is stopped,
///  because the remaining part of the response would be taken for the next one.
/// </remarks>
internal sealed class CatFileSession : IDisposable
{
    private const int MaxHeaderLength = 64 * 1024;
    private const long MaxContentLength = 256 * 1024 * 1024;
    private const long MinBytesPerSecond = 1024 * 1024;

    // The start of the process can take long, e.g. because of a virus scanner
    private static readonly TimeSpan _defaultFirstResponseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _responseTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding _encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly BrokerProcess _process;
    private readonly byte[] _buffer = new byte[MaxHeaderLength];
    private int _bufferStart;
    private int _bufferEnd;
    private TimeSpan _timeout;

    internal CatFileSession(IProcess process, TimeSpan? firstResponseTimeout = null)
    {
        _process = new BrokerProcess(process);
        _timeout = firstResponseTimeout ?? _defaultFirstResponseTimeout;
    }

    public bool IsStopped => _process.IsStopped;

    /// <summary>
    ///  Starts the process.
    /// </summary>
    /// <remarks>
    ///  The current directory of the process is not the one of the repository,
    ///  because it could not be moved or deleted while the process is running.
    /// </remarks>
    public static CatFileSession Start(Executable gitExecutable, string gitDir)
    {
        Executable executable = new(gitExecutable.Command, Environment.SystemDirectory);
        GitArgumentBuilder args = new("cat-file", gitOptions: (ArgumentString)$"--git-dir={gitDir.ToPosixPath().TrimEnd('/').Quote()}")
        {
            "--batch-command"
        };

        return new CatFileSession(executable.Start(args, redirectInput: true, redirectOutput: true, outputEncoding: _encoding, throwOnErrorExit: false));
    }

    /// <summary>
    ///  Gets the header of an object.
    /// </summary>
    /// <param name="objectName">Any name of an object as supported by git.</param>
    /// <returns><see langword="null"/> if the object is missing or the name is not supported.</returns>
    /// <exception cref="GitBrokerException">The session is stopped.</exception>
    /// <exception cref="OperationCanceledException">Also stops the session if the request has been sent.</exception>
    public GitObjectInfo? GetInfo(string objectName, CancellationToken cancellationToken)
        => TryGetRequest("info", objectName, out byte[] request)
            ? Execute(request, _ => ReadHeader(objectName), cancellationToken)
            : null;

    /// <summary>
    ///  Gets the content of a blob.
    /// </summary>
    /// <returns><see langword="null"/> if the object is missing, not a blob, or too large.</returns>
    /// <exception cref="GitBrokerException">The session is stopped.</exception>
    /// <exception cref="OperationCanceledException">Also stops the session if the request has been sent.</exception>
    public byte[]? GetBlob(ObjectId objectId, CancellationToken cancellationToken)
    {
        string name = objectId.ToString();
        return TryGetRequest("contents", name, out byte[] request)
            ? Execute(request, ReadContent, cancellationToken)
            : null;

        byte[]? ReadContent(CancellationTokenSource timeout)
        {
            if (ReadHeader(name) is not GitObjectInfo info)
            {
                return null;
            }

            if (info.ObjectId != objectId || info.Type == GitObjectInfo.Submodule)
            {
                throw new GitBrokerException($"Unexpected object '{info}' instead of '{name}'");
            }

            if (info.Size > MaxContentLength)
            {
                _process.Stop(kill: true);
                return null;
            }

            timeout.CancelAfter(_responseTimeout + TimeSpan.FromSeconds(info.Size / MinBytesPerSecond));
            byte[] content = new byte[info.Size];
            ReadExactly(content);
            if (ReadByte() != '\n')
            {
                throw new GitBrokerException($"Unexpected end of '{name}'");
            }

            return info.Type == GitObjectInfo.Blob ? content : null;
        }
    }

    /// <summary>
    ///  Stops the process. Can be called on any thread.
    /// </summary>
    public void Dispose() => _process.Stop(kill: false);

    public void KillNow() => _process.KillNow();

    private static bool TryGetRequest(string command, string objectName, out byte[] request)
    {
        request = [];
        if (objectName.Length == 0 || objectName.Length > MaxHeaderLength / 8 || objectName.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            request = _encoding.GetBytes($"{command} {objectName}\n");
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private T Execute<T>(byte[] request, Func<CancellationTokenSource, T> readResponse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopped)
        {
            throw new GitBrokerException("The process has been stopped");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        _timeout = _responseTimeout;

        // Unblocks the read and write operations
        using CancellationTokenRegistration registration = timeout.Token.Register(() => _process.Stop(kill: true));
        try
        {
            GitBrokerStats.CountRequest();
            _process.Input.Write(request);
            _process.Input.Flush();
            return readResponse(timeout);
        }
        catch (Exception ex)
        {
            _process.Stop(kill: true);
            cancellationToken.ThrowIfCancellationRequested();
            throw ex as GitBrokerException ?? new GitBrokerException("Request failed", ex);
        }
    }

    private GitObjectInfo? ReadHeader(string objectName)
    {
        string header = ReadLine();
        if (header == $"{objectName} missing" || header == $"{objectName} ambiguous")
        {
            return null;
        }

        string[] parts = header.Split(' ');
        if (parts[0].Length == ObjectId.Sha1CharCount && ObjectId.TryParse(parts[0], out ObjectId objectId))
        {
            if (parts is [_, "blob" or "tree" or "commit" or "tag", _]
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long size))
            {
                return new GitObjectInfo(objectId, parts[1], size);
            }

            if (parts is [_, GitObjectInfo.Submodule])
            {
                return new GitObjectInfo(objectId, GitObjectInfo.Submodule, Size: 0);
            }
        }

        throw new GitBrokerException($"Unexpected header '{header}'");
    }

    private string ReadLine()
    {
        using MemoryStream line = new();
        for (byte value = ReadByte(); value != '\n'; value = ReadByte())
        {
            line.WriteByte(value);
            if (line.Length > MaxHeaderLength)
            {
                throw new GitBrokerException("The header is too long");
            }
        }

        return _encoding.GetString(line.GetBuffer(), 0, (int)line.Length);
    }

    private byte ReadByte()
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

    private void ReadExactly(Span<byte> content)
    {
        int buffered = Math.Min(content.Length, _bufferEnd - _bufferStart);
        _buffer.AsSpan(_bufferStart, buffered).CopyTo(content);
        _bufferStart += buffered;
        _process.Output.ReadExactly(content[buffered..]);
    }
}
