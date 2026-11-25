using System.Diagnostics.CodeAnalysis;
using System.Text;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;

namespace GitCommands.Git.Broker;

/// <summary>
///  Reads objects with a persistent git process per repository instead of starting a process for each object.
/// </summary>
/// <remarks>
///  Whatever is not answered must be read with a git process of its own: the result is always the same.
/// </remarks>
public static class GitBroker
{
    private const int MaxSessions = 8;
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(15);

    // Short, because the working directory is locked by the process
    private static readonly TimeSpan _attributesIdleTimeout = TimeSpan.FromSeconds(3);

    internal static GitBrokerPool Pool { get; } = new(_idleTimeout, MaxSessions);

    internal static CheckAttrPool AttributesPool { get; } = new(_attributesIdleTimeout, MaxSessions);

    internal static BlobMemoryCache Cache { get; } = new(() => Math.Max(0L, AppSettings.GitBrokerMemoryLimit.Value) * 1024 * 1024);

    /// <summary>
    ///  Gets the id of a single file or submodule in a commit, like <c>git ls-tree -r</c>.
    /// </summary>
    /// <returns><see langword="false"/> if the persistent process cannot be used, or there is no such file or submodule.</returns>
    public static bool TryGetFileTreeId(IGitModule module, ObjectId commitId, string? fileName, out ObjectId treeId, out bool isSubmodule, CancellationToken cancellationToken)
    {
        treeId = default;
        isSubmodule = false;
        if (commitId.IsZeroOrArtificial || commitId == ObjectId.CombinedDiffId || !IsPlainPath(fileName) || !IsSupported(module.GitExecutable, out Executable? git))
        {
            return false;
        }

        if (!Pool.TryRequest(git, (session, token) => session.GetInfo($"{commitId}:{fileName}", token), cancellationToken, out GitObjectInfo? info)
            || info is not { Type: GitObjectInfo.Blob or GitObjectInfo.Submodule })
        {
            // E.g. a folder, for which git ls-tree returns the files.
            // Nor is a commit returned, although the commit of a submodule could be known in this repository:
            // it could also be the one a name like "<path>-g<id>" stands for.
            GitBrokerStats.CountFallback();
            return false;
        }

        treeId = info.Value.ObjectId;
        isSubmodule = info.Value.Type == GitObjectInfo.Submodule;
        return true;
    }

    /// <summary>
    ///  Gets a module which provides the output of <c>git check-attr</c> for the file without starting a process.
    /// </summary>
    /// <returns>The <paramref name="module"/> itself if the persistent process cannot be used.</returns>
    internal static IGitModule WithAttributes(IGitModule module, string fileName)
    {
        if (!AppSettings.UseGitBrokerForAttributes.Value
            || !IsPlainPath(fileName)
            || !AttributeFiles.IsSupportedPath(fileName)
            || !IsSupported(module.GitExecutable, out Executable? git))
        {
            return module;
        }

        // The second attempt is for a process which has been stopped because it was outdated
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool isOutdated = false;
            bool isAnswered = AttributesPool.TryRequest(
                git,
                (session, token) =>
                {
                    byte[]? response = session.Query(fileName, token);
                    isOutdated = session.IsStopped;
                    return response;
                },
                CancellationToken.None,
                out byte[]? output);
            if (isAnswered)
            {
                return KnownAttributesModule.Create(module, output!);
            }

            if (!isOutdated)
            {
                break;
            }
        }

        GitBrokerStats.CountFallback();
        return module;
    }

    /// <summary>
    ///  Ends all persistent processes without waiting for them, because a command could be hindered by them (e.g. <c>git gc</c>).
    /// </summary>
    /// <remarks>
    ///  All, because worktrees share their objects. The next request starts a process again.
    /// </remarks>
    public static void ReleaseAll()
    {
        Pool.ReleaseAll();
        AttributesPool.ReleaseAll();
    }

    /// <summary>
    ///  Gets the content of a blob as the output of <c>git cat-file blob</c> would be decoded.
    /// </summary>
    internal static bool TryGetFileText(IExecutable executable, ObjectId blobId, Encoding encoding, bool stripAnsiEscapeCodes, [NotNullWhen(true)] out string? text)
    {
        text = IsSupported(executable, out Executable? git) && TryGetBlob(git, blobId, CancellationToken.None) is byte[] content
            ? ExecutableExtensions.DecodeOutput(content, encoding, stripAnsiEscapeCodes)
            : null;
        return text is not null;
    }

    /// <summary>
    ///  Gets the content of a blob, <see langword="null"/> if it has to be read with <c>git cat-file blob</c>.
    /// </summary>
    internal static async Task<MemoryStream?> TryGetBlobStreamAsync(string blob, IGitCommandRunner gitCommandRunner, CancellationToken cancellationToken)
    {
        if (gitCommandRunner is not GitCommandRunner runner
            || !IsSupported(runner.Executable, out Executable? git)
            || blob.Length != ObjectId.Sha1CharCount
            || !ObjectId.TryParse(blob, out ObjectId blobId))
        {
            return null;
        }

        if (!ThreadHelper.HasJoinableTaskContext)
        {
            return null;
        }

        // Nothing but the memory is used on the UI thread
        byte[]? content = ThreadHelper.JoinableTaskContext.IsOnMainThread
            ? TryGetBlob(git, blobId, cancellationToken)
            : await Task.Run(() => TryGetBlob(git, blobId, cancellationToken), cancellationToken);
        if (content is null)
        {
            return null;
        }

        // A copy: the caller may change the stream
        MemoryStream stream = new(content.Length);
        stream.Write(content);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    ///  Gets whether a path means the same for <c>git ls-tree</c> and in <c>commit:path</c>.
    /// </summary>
    private static bool IsPlainPath([NotNullWhen(true)] string? path)
        => !string.IsNullOrEmpty(path)
            && path[0] != ':'
            && !path.Any(c => char.IsControl(c) || c is '\\' or '"')
            && !path.Contains("$QUOTE$", StringComparison.Ordinal)
            && path.Split('/').All(part => part is not ("" or "." or ".."));

    private static bool IsSupported(IExecutable? executable, [NotNullWhen(true)] out Executable? git)
    {
        git = executable as Executable;
        return git is { PrefixArguments.Length: 0 } && AppSettings.UseGitBroker.Value;
    }

    private static byte[]? TryGetBlob(Executable git, ObjectId blobId, CancellationToken cancellationToken)
    {
        if (!GitBrokerPool.TryGetKey(git.WorkingDir, out string? key))
        {
            return null;
        }

        if (Cache.TryGet(key, blobId, out byte[]? content))
        {
            GitBrokerStats.CountCacheHit();
            return content;
        }

        if (!Pool.TryRequest(git, (session, token) => session.GetBlob(blobId, token), cancellationToken, out content) || content is null)
        {
            GitBrokerStats.CountFallback();
            return null;
        }

        Cache.Add(key, blobId, content);
        return content;
    }
}
