using System.Diagnostics.CodeAnalysis;
using System.Text;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitCommands.Git.Broker;

/// <summary>
///  A <c>git check-attr --stdin</c> process of a repository for the attributes which tell whether a file is binary.
/// </summary>
internal sealed class CheckAttrSession : BrokerSession
{
    private static readonly string[] _attributes = ["diff", "text", "crlf", "eol"];

    private readonly AttributeFiles _files;

    internal CheckAttrSession(IProcess process, AttributeFiles files)
        : base(process, firstResponseTimeout: null)
    {
        _files = files;
    }

    /// <summary>
    ///  Starts the process.
    /// </summary>
    /// <remarks>
    ///  Git changes to the working directory in any case, so it is also the current directory of the process,
    ///  and git checks the repository (its owner) as usual. The folder cannot be moved or deleted while the process is running.
    ///  Neither missing objects are fetched nor the fsmonitor hook is run when the index is read.
    /// </remarks>
    /// <returns><see langword="null"/> if it would not be noticed when the attributes are changed.</returns>
    public static CheckAttrSession? TryStart(Executable gitExecutable, GitBrokerRepository repository)
    {
        if (AttributeFiles.TryCreate(gitExecutable, repository) is not AttributeFiles files)
        {
            return null;
        }

        GitArgumentBuilder args = new("check-attr", gitOptions: (ArgumentString)"--no-lazy-fetch -c core.fsmonitor=false")
        {
            "--stdin",
            "-z",
            string.Join(' ', _attributes)
        };

        return new CheckAttrSession(
            gitExecutable.Start(args, redirectInput: true, redirectOutput: true, outputEncoding: RequestEncoding, throwOnErrorExit: false),
            files);
    }

    /// <summary>
    ///  Gets whether a path can be requested.
    /// </summary>
    public static bool TryGetRequest(string path, [NotNullWhen(true)] out byte[]? request)
    {
        request = null;
        if (path.Length > BufferLength / 8 || path.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            request = RequestEncoding.GetBytes($"{path}\0");
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    ///  Gets the output of <c>git check-attr -z</c> for a path.
    /// </summary>
    /// <returns>
    ///  <see langword="null"/> if it has to be read by git itself. The session is stopped if it is outdated.
    /// </returns>
    /// <exception cref="GitBrokerException">The session is stopped.</exception>
    /// <exception cref="OperationCanceledException">Also stops the session if the response cannot be completed soon.</exception>
    public byte[]? Query(string path, CancellationToken cancellationToken)
    {
        if (!TryGetRequest(path, out byte[]? request))
        {
            return null;
        }

        if (!_files.IsCurrent(path))
        {
            Dispose();
            return null;
        }

        return _files.TryAddFoldersOf(path)
            ? Execute(request, _ => ReadResponse(request), cancellationToken)
            : null;
    }

    private byte[] ReadResponse(byte[] terminatedPath)
    {
        using MemoryStream response = new();
        foreach (string attribute in _attributes)
        {
            ReadExpected(terminatedPath);
            ReadExpected(RequestEncoding.GetBytes($"{attribute}\0"));

            // The value
            for (byte value = Read(); value != 0; value = Read())
            {
            }
        }

        return response.ToArray();

        void ReadExpected(byte[] expected)
        {
            foreach (byte value in expected)
            {
                if (Read() != value)
                {
                    throw new GitBrokerException("Unexpected response");
                }
            }
        }

        byte Read()
        {
            byte value = ReadByte();
            response.WriteByte(value);
            if (response.Length > BufferLength * 2)
            {
                throw new GitBrokerException("The response is too long");
            }

            return value;
        }
    }
}
