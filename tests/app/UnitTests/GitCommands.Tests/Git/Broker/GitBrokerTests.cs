using System.Reflection;
using System.Text;
using GitCommands;
using GitCommands.Git.Broker;
using GitCommands.Logging;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUI;
using Microsoft.VisualStudio.Threading;
using NSubstitute;

namespace GitCommandsTests.Git.Broker;

/// <summary>
///  Compares the results of the persistent process with the ones of a process per request.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GitBrokerTests
{
    private const string UnknownId = "1111111111111111111111111111111111111111";

    private static readonly string[] _files =
    [
        "file.txt",
        "dir with space/a b.txt",
        "폴더/한글 파일.txt",
        "dir/[a].txt",
        "dir/file",
        "a~1^2@{0}.txt",
        "-dash",
        "name-gabcdef1",
        " leading",
    ];

    private GitBrokerTestScope _scope = null!;

    private static IEnumerable<TestCaseData> Contents()
    {
        yield return new TestCaseData(Array.Empty<byte>()).SetArgDisplayNames("empty");
        yield return new TestCaseData("a\r\nb\rc\n"u8.ToArray()).SetArgDisplayNames("line endings");
        yield return new TestCaseData(new byte[] { 0xEF, 0xBB, 0xBF, 65, 10 }).SetArgDisplayNames("BOM");
        yield return new TestCaseData(new byte[] { 0, 1, 0, 255, 10, 0 }).SetArgDisplayNames("NUL");
        yield return new TestCaseData("한글 ü 😀\n"u8.ToArray()).SetArgDisplayNames("UTF-8");
        yield return new TestCaseData(new byte[] { 0xC7, 0xD1, 0xB1, 0xDB, 0xFC }).SetArgDisplayNames("not UTF-8");
        yield return new TestCaseData("\u001b[31mred\u001b[m text\n"u8.ToArray()).SetArgDisplayNames("ANSI escape codes");
        yield return new TestCaseData("version https://git-lfs.github.com/spec/v1\nnot a pointer\n"u8.ToArray()).SetArgDisplayNames("LFS prefix");
        byte[] large = new byte[(2 * 1024 * 1024) + 3];
        new Random(2).NextBytes(large);
        yield return new TestCaseData(large).SetArgDisplayNames("2 MB binary");
        yield return new TestCaseData(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("한글 line\r\n", 200_000)))).SetArgDisplayNames("2.6 MB text");
    }

    [SetUp]
    public void SetUp()
    {
        _scope = new GitBrokerTestScope();
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "no process may remain");
    }

    [Test]
    public void Settings_should_have_the_expected_defaults()
    {
        AppSettings.UseGitBroker.Default.Should().BeTrue();
        AppSettings.GitBrokerMemoryLimit.Default.Should().Be(512);
    }

    [TestCaseSource(nameof(Contents))]
    public void GetFileText_should_be_the_same_as_without_broker(byte[] content)
    {
        ObjectId blobId = _scope.AddBlob(content);

        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Latin1, Encoding.Unicode })
        {
            foreach (bool stripAnsiEscapeCodes in new[] { true, false })
            {
                (string? expected, string? actual, string? cached) = Compare(() => _scope.Module.GetFileText(blobId, encoding, stripAnsiEscapeCodes));

                expected.Should().NotBeNull();
                string.Equals(actual, expected, StringComparison.Ordinal).Should().BeTrue();
                string.Equals(cached, expected, StringComparison.Ordinal).Should().BeTrue();
            }
        }
    }

    [TestCaseSource(nameof(Contents))]
    public void GetFileStream_should_be_the_same_as_without_broker(byte[] content)
    {
        ObjectId blobId = _scope.AddBlob(content);

        (byte[]? expected, byte[]? actual, byte[]? cached) = Compare(() => GetFileStream(blobId.ToString()));

        expected.AsSpan().SequenceEqual(content).Should().BeTrue();
        actual.AsSpan().SequenceEqual(content).Should().BeTrue();
        cached.AsSpan().SequenceEqual(content).Should().BeTrue();
    }

    [Test]
    public void GetFileText_should_use_one_process_for_several_blobs_and_none_for_known_blobs()
    {
        ObjectId[] blobIds = [.. Enumerable.Range(0, 5).Select(i => _scope.AddBlob(Encoding.UTF8.GetBytes($"blob {i}")))];
        CommandLog.Clear();

        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < blobIds.Length; i++)
            {
                GetFileText(blobIds[i]).Should().Be($"blob {i}");
            }
        }

        CommandLog.Commands.Should().ContainSingle().Which.Arguments.Should().EndWith("cat-file --batch-command");
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void GetFileText_should_use_contents_in_memory_after_the_release_also_on_the_UI_thread()
    {
        ObjectId blobId = _scope.AddBlob("text"u8.ToArray());
        GetFileText(blobId).Should().Be("text");
        GitBroker.ReleaseAll();
        GitModule.GitCommandCache.Clear();
        CommandLog.Clear();
        ThreadHelper.JoinableTaskContext.IsOnMainThread.Should().BeTrue();

        _scope.Module.GetFileText(blobId, Encoding.UTF8, stripAnsiEscapeCodes: false).Should().Be("text");

        CommandLog.Commands.Should().BeEmpty();
    }

    [Test]
    public void GetFileText_should_not_keep_contents_if_the_cache_is_disabled()
    {
        AppSettings.GitBrokerMemoryLimit.Value = 0;
        ObjectId blobId = _scope.AddBlob("text"u8.ToArray());
        int requests = GitBrokerStats.Requests;

        GetFileText(blobId).Should().Be("text");
        GetFileText(blobId).Should().Be("text");

        (GitBrokerStats.Requests - requests).Should().Be(2);
        GitBroker.Cache.TotalBytes.Should().Be(0);
    }

    [Test]
    public void Missing_and_other_objects_should_be_the_same_as_without_broker()
    {
        ObjectId commitId = _scope.Commit("dir/file", "text");
        string[] objects = [UnknownId, commitId.ToString(), _scope.Git("rev-parse HEAD:dir")];
        int requests = GitBrokerStats.Requests;

        foreach (string id in objects)
        {
            (string? expectedText, string? text, string? textAgain) = Compare(() => _scope.Module.GetFileText(ObjectId.Parse(id), Encoding.UTF8, true));
            (text, textAgain).Should().Be((expectedText, expectedText));
        }

        foreach (string id in (string[])[.. objects, "HEAD:dir/file", "HEAD:missing"])
        {
            (byte[]? expected, byte[]? actual, byte[]? again) = Compare(() => GetFileStream(id));
            actual.Should().Equal(expected);
            again.Should().Equal(expected);
        }

        GitBrokerStats.Requests.Should().BeGreaterThan(requests);
    }

    [Test]
    public void Object_created_after_it_was_missing_should_be_found()
    {
        byte[] content = "created later"u8.ToArray();
        ObjectId blobId;
        using (GitBrokerTestScope other = new())
        {
            blobId = other.AddBlob(content);
        }

        GetFileText(_scope.AddBlob("first"u8.ToArray())).Should().Be("first");
        GetFileText(blobId).Should().BeNull();

        _scope.AddBlob(content).Should().Be(blobId);

        int hits = GitBrokerStats.Fallbacks;
        GetFileText(blobId).Should().Be("created later");
        GitBrokerStats.Fallbacks.Should().Be(hits);
    }

    [Test]
    public void GetFileStream_should_return_null_if_cancelled()
    {
        ObjectId blobId = _scope.AddBlob("text"u8.ToArray());

        GitBrokerTestScope.InBackground(() => _scope.Module.GetFileStreamAsync(blobId.ToString(), new CancellationToken(canceled: true)))
            .Should().BeNull();
    }

    [Test]
    public void GetFileStream_should_return_independent_streams()
    {
        ObjectId blobId = _scope.AddBlob("text"u8.ToArray());

        using MemoryStream first = GitBrokerTestScope.InBackground(() => _scope.Module.GetFileStreamAsync(blobId.ToString(), CancellationToken.None))!;
        first.CanWrite.Should().BeTrue("plugins may change the stream");
        first.GetBuffer()[0] = (byte)'x';
        first.Write("changed and longer"u8);
        using MemoryStream second = GitBrokerTestScope.InBackground(() => _scope.Module.GetFileStreamAsync(blobId.ToString(), CancellationToken.None))!;

        second.Position.Should().Be(0);
        second.ToArray().Should().Equal("text"u8.ToArray());
    }

    [Test]
    public void TryGetBlobStream_should_not_throw_without_JoinableTaskContext()
    {
        ObjectId blobId = _scope.AddBlob("text"u8.ToArray());
        FieldInfo field = typeof(ThreadHelper).GetField("_taskManager", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? taskManager = field.GetValue(null);
        JoinableTaskFactory factory = ThreadHelper.JoinableTaskFactory;
        MemoryStream? stream;
        field.SetValue(null, null);
        try
        {
            stream = factory.Run(() => GitBroker.TryGetBlobStreamAsync(blobId.ToString(), _scope.Module.GitCommandRunner, CancellationToken.None));
        }
        finally
        {
            field.SetValue(null, taskManager);
        }

        stream.Should().BeNull("git has to be run");
    }

    [Test]
    public void TryGetFileTreeId_should_handle_plain_files_like_ls_tree()
    {
        ObjectId commitId = CommitFiles();

        foreach (string path in (string[])[.. _files, "link"])
        {
            IObjectGitItem expected = GetTree(commitId, path).Should().ContainSingle().Subject;

            TryGetFileTreeId(commitId, path, out ObjectId treeId, out bool isSubmodule).Should().BeTrue(path);

            treeId.Should().Be(expected.ObjectId, path);
            isSubmodule.Should().BeFalse(path);
        }
    }

    [TestCase("missing")]
    [TestCase("File.TXT")]
    [TestCase("dir with space")]
    [TestCase("dir with space/")]
    [TestCase("./file.txt")]
    [TestCase("dir/../file.txt")]
    [TestCase("dir//file")]
    [TestCase("/file.txt")]
    [TestCase("dir\\file")]
    [TestCase(":file.txt")]
    [TestCase(":/file.txt")]
    [TestCase(":(top)file.txt")]
    [TestCase("*.txt")]
    [TestCase("")]
    [TestCase("a\"b")]
    [TestCase("file.txt\n")]
    [TestCase("file.txt ")]
    public void TryGetFileTreeId_should_leave_everything_else_to_ls_tree(string path)
    {
        TryGetFileTreeId(CommitFiles(), "file.txt", out _, out _).Should().BeTrue();

        TryGetFileTreeId(CommitFiles(), path, out _, out _).Should().BeFalse();
    }

    [Test]
    public void TryGetFileTreeId_should_handle_submodules_like_ls_tree([Values] bool isCommitKnown)
    {
        ObjectId submoduleId = isCommitKnown ? _scope.Commit("file", "text") : ObjectId.Parse(UnknownId);
        _scope.AddToIndex("160000", submoduleId, "sub");
        ObjectId commitId = _scope.Commit("other", "text");
        IObjectGitItem expected = GetTree(commitId, "sub").Should().ContainSingle().Subject;
        expected.ObjectType.Should().Be(GitObjectType.Commit);

        if (TryGetFileTreeId(commitId, "sub", out ObjectId treeId, out bool isSubmodule))
        {
            treeId.Should().Be(expected.ObjectId);
            isSubmodule.Should().BeTrue();
        }
    }

    [Test]
    public void TryGetFileTreeId_should_not_handle_artificial_or_unknown_commits()
    {
        _scope.Commit("file", "text");

        foreach (ObjectId id in new[] { default, ObjectId.WorkTreeId, ObjectId.IndexId, ObjectId.CombinedDiffId, ObjectId.Parse(UnknownId) })
        {
            TryGetFileTreeId(id, "file", out _, out _).Should().BeFalse();
        }
    }

    [Test]
    public void TryGetFileTreeId_should_not_handle_a_mocked_module()
    {
        IGitModule module = Substitute.For<IGitModule>();

        GitBrokerTestScope.InBackground(() => GitBroker.TryGetFileTreeId(module, ObjectId.Parse(UnknownId), "file", out _, out _, CancellationToken.None))
            .Should().BeFalse();
    }

    [Test]
    public void Disabled_broker_should_not_do_anything()
    {
        AppSettings.UseGitBroker.Value = false;
        ObjectId commitId = _scope.Commit("file", "text");
        ObjectId blobId = ObjectId.Parse(_scope.Git("rev-parse HEAD:file"));
        (int Starts, int Requests, int Hits) expected = (GitBrokerStats.Starts, GitBrokerStats.Requests, GitBrokerStats.CacheHits);

        GetFileText(blobId).Should().Be("text");
        GetFileStream(blobId.ToString()).Should().Equal("text"u8.ToArray());
        TryGetFileTreeId(commitId, "file", out _, out _).Should().BeFalse();

        (GitBrokerStats.Starts, GitBrokerStats.Requests, GitBrokerStats.CacheHits).Should().Be(expected);

        AppSettings.UseGitBroker.Value = true;
        TryGetFileTreeId(commitId, "file", out _, out _).Should().BeTrue();
    }

    private static (T Expected, T Actual, T Cached) Compare<T>(Func<T> func)
    {
        GitModule.GitCommandCache.Clear();
        AppSettings.UseGitBroker.Value = false;
        T expected = GitBrokerTestScope.InBackground(func);
        AppSettings.UseGitBroker.Value = true;
        GitModule.GitCommandCache.Clear();
        GitBroker.Cache.Clear();
        int requests = GitBrokerStats.Requests;
        int hits = GitBrokerStats.CacheHits;

        T actual = GitBrokerTestScope.InBackground(func);
        T cached = GitBrokerTestScope.InBackground(func);

        if (expected is string || expected is byte[] { Length: > 100 })
        {
            (GitBrokerStats.Requests - requests).Should().Be(1, "the same way must not be compared");
            (GitBrokerStats.CacheHits - hits).Should().Be(1, "the same way must not be compared");
        }

        return (expected, actual, cached);
    }

    private ObjectId CommitFiles()
    {
        _scope.AddToIndex("120000", _scope.AddBlob("file.txt"u8.ToArray()), "link");
        return _scope.Commit([.. _files.Select(f => (f, $"content of {f}"))]);
    }

    private string? GetFileText(ObjectId blobId)
        => GitBrokerTestScope.InBackground(() => _scope.Module.GetFileText(blobId, Encoding.UTF8, stripAnsiEscapeCodes: false));

    private byte[]? GetFileStream(string blob)
    {
        using MemoryStream? stream = GitBrokerTestScope.InBackground(() => _scope.Module.GetFileStreamAsync(blob, CancellationToken.None));
        return stream?.ToArray();
    }

    private IObjectGitItem[] GetTree(ObjectId commitId, string path) => [.. _scope.Module.GetTree(commitId, full: true, path)];

    private bool TryGetFileTreeId(ObjectId commitId, string path, out ObjectId treeId, out bool isSubmodule)
    {
        (bool found, treeId, isSubmodule) = GitBrokerTestScope.InBackground(() =>
        {
            bool found = GitBroker.TryGetFileTreeId(_scope.Module, commitId, path, out ObjectId id, out bool isSub, CancellationToken.None);
            return (found, id, isSub);
        });
        return found;
    }
}
