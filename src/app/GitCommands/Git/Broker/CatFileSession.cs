using System.Globalization;
using System.Text;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitCommands.Git.Broker;

/// <summary>
///  A <c>git cat-file --batch-command</c> process of a repository.
/// </summary>
internal sealed class CatFileSession : BrokerSession
{
    private const long MaxContentLength = 256 * 1024 * 1024;
    private const long MaxCancelledContentLength = 1024 * 1024;
    private const long MinBytesPerSecond = 1024 * 1024;

    internal CatFileSession(IProcess process, TimeSpan? firstResponseTimeout = null)
        : base(process, firstResponseTimeout)
    {
    }

    /// <summary>
    ///  Starts the process.
    /// </summary>
    /// <remarks>
    ///  The current directory of the process is not the one of the repository,
    ///  because it could not be moved or deleted while the process is running.
    ///  Missing objects are not fetched (which could run commands set in the config of the repository): the option sets
    ///  GIT_NO_LAZY_FETCH for this process. It ends at once if git does not know the option.
    ///  The index is not read for the supported names, nevertheless the fsmonitor hook (run when it is read) is disabled.
    /// </remarks>
    public static CatFileSession Start(Executable gitExecutable, string gitDir) => new(StartProcess(gitExecutable, gitDir));

    internal static IProcess StartProcess(Executable gitExecutable, string gitDir)
    {
        Executable executable = new(gitExecutable.Command, Environment.SystemDirectory);
        GitArgumentBuilder args = new("cat-file", gitOptions: (ArgumentString)$"--git-dir={gitDir.ToPosixPath().TrimEnd('/').Quote()} --no-lazy-fetch -c core.fsmonitor=false")
        {
            "--batch-command"
        };

        return executable.Start(args, redirectInput: true, redirectOutput: true, outputEncoding: RequestEncoding, throwOnErrorExit: false);
    }

    /// <summary>
    ///  Gets the header of an object.
    /// </summary>
    /// <param name="objectName">Any name of an object as supported by git, except for names in the index.</param>
    /// <returns><see langword="null"/> if the object is missing or the name is not supported.</returns>
    /// <exception cref="GitBrokerException">The session is stopped.</exception>
    /// <exception cref="OperationCanceledException">Also stops the session if the response cannot be completed soon.</exception>
    public GitObjectInfo? GetInfo(string objectName, CancellationToken cancellationToken)
        => TryGetRequest("info", objectName, out byte[] request)
            ? Execute(request, _ => ReadHeader(objectName), cancellationToken)
            : null;

    /// <summary>
    ///  Gets the content of a blob.
    /// </summary>
    /// <returns><see langword="null"/> if the object is missing, not a blob, or too large.</returns>
    /// <exception cref="GitBrokerException">The session is stopped.</exception>
    /// <exception cref="OperationCanceledException">Also stops the session if the response cannot be completed soon.</exception>
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
                Kill();
                return null;
            }

            timeout.CancelAfter(ResponseTimeout + TimeSpan.FromSeconds(info.Size / MinBytesPerSecond));

            // If it is cancelled after this check, the time is limited by the cancellation
            if (cancellationToken.IsCancellationRequested)
            {
                timeout.CancelAfter(CancelledResponseTimeout);
                if (info.Size > MaxCancelledContentLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            byte[] content = new byte[info.Size];
            ReadExactly(content);
            if (ReadByte() != '\n')
            {
                throw new GitBrokerException($"Unexpected end of '{name}'");
            }

            return info.Type == GitObjectInfo.Blob ? content : null;
        }
    }

    private static bool TryGetRequest(string command, string objectName, out byte[] request)
    {
        request = [];
        if (objectName.Length == 0 || objectName.TrimStart().StartsWith(':') || objectName.Length > BufferLength / 8 || objectName.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            request = RequestEncoding.GetBytes($"{command} {objectName}\n");
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
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
            if (line.Length > BufferLength)
            {
                throw new GitBrokerException("The header is too long");
            }
        }

        return RequestEncoding.GetString(line.GetBuffer(), 0, (int)line.Length);
    }
}
