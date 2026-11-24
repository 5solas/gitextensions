using System.Diagnostics;
using System.Text;
using GitCommands;
using GitCommands.Git;
using GitCommands.Git.Broker;
using GitCommands.Git.Extensions;
using GitCommands.Logging;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitCommandsTests.Git.Broker;

[TestFixture]
[NonParallelizable]
public sealed class CatFileSessionTests
{
    private const string Id1 = "1111111111111111111111111111111111111111";

    private GitBrokerTestScope _scope = null!;

    private static IEnumerable<TestCaseData> Contents()
    {
        yield return new TestCaseData(Array.Empty<byte>()).SetArgDisplayNames("empty");
        yield return new TestCaseData("a"u8.ToArray()).SetArgDisplayNames("one byte");
        yield return new TestCaseData("line\n"u8.ToArray()).SetArgDisplayNames("trailing LF");
        yield return new TestCaseData("a\r\nb\rc\n\n"u8.ToArray()).SetArgDisplayNames("mixed line endings");
        yield return new TestCaseData(new byte[] { 0, 1, 0, 255, 10, 0 }).SetArgDisplayNames("NUL");
        yield return new TestCaseData(new byte[] { 0xEF, 0xBB, 0xBF, 65 }).SetArgDisplayNames("BOM");
        yield return new TestCaseData("1111111111111111111111111111111111111111 missing\n"u8.ToArray()).SetArgDisplayNames("looks like a response");
        byte[] large = new byte[(3 * 1024 * 1024) + 7];
        new Random(1).NextBytes(large);
        yield return new TestCaseData(large).SetArgDisplayNames("3 MB");
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

    [TestCaseSource(nameof(Contents))]
    public void GetBlob_should_return_exactly_the_bytes_also_several_times(byte[] content)
    {
        ObjectId blobId = _scope.AddBlob(content);
        ObjectId otherId = _scope.AddBlob("other"u8.ToArray());
        using CatFileSession session = _scope.StartSession();

        // The following request is correct only if the framing is
        for (int i = 0; i < 2; i++)
        {
            session.GetBlob(blobId, CancellationToken.None)!.AsSpan().SequenceEqual(content).Should().BeTrue();
            session.GetBlob(otherId, CancellationToken.None).Should().Equal("other"u8.ToArray());
        }
    }

    [Test]
    public void GetBlob_should_return_null_for_objects_other_than_blobs_and_stay_usable()
    {
        ObjectId commitId = _scope.Commit("dir/file", "text");
        ObjectId treeId = ObjectId.Parse(_scope.Git("rev-parse HEAD^{tree}"));
        ObjectId blobId = ObjectId.Parse(_scope.Git("rev-parse HEAD:dir/file"));
        using CatFileSession session = _scope.StartSession();

        session.GetBlob(ObjectId.Parse(Id1), CancellationToken.None).Should().BeNull();
        session.GetBlob(treeId, CancellationToken.None).Should().BeNull();
        session.GetBlob(commitId, CancellationToken.None).Should().BeNull();
        session.IsStopped.Should().BeFalse();
        session.GetBlob(blobId, CancellationToken.None).Should().Equal("text"u8.ToArray());
    }

    [Test]
    public void GetInfo_should_return_the_header()
    {
        ObjectId submoduleId = ObjectId.Parse("2222222222222222222222222222222222222222");
        _scope.AddToIndex("160000", submoduleId, "sub");
        ObjectId commitId = _scope.Commit("dir with space/파일 1.txt", "text");
        ObjectId blobId = ObjectId.Parse(_scope.Git("rev-parse \"HEAD:dir with space/파일 1.txt\""));
        using CatFileSession session = _scope.StartSession();

        session.GetInfo($"{commitId}:dir with space/파일 1.txt", CancellationToken.None).Should().Be(new GitObjectInfo(blobId, "blob", 4));
        session.GetInfo($"{commitId}:dir with space", CancellationToken.None)!.Value.Type.Should().Be("tree");
        session.GetInfo($"{commitId}:missing", CancellationToken.None).Should().BeNull();

        // Older git versions report it as missing
        GitObjectInfo? submodule = session.GetInfo($"{commitId}:sub", CancellationToken.None);
        (submodule is null || submodule == new GitObjectInfo(submoduleId, "submodule", 0)).Should().BeTrue();
        session.GetInfo($"{commitId}:dir with space/파일 1.txt", CancellationToken.None)!.Value.ObjectId.Should().Be(blobId);
    }

    [Test]
    public void GetInfo_should_not_send_names_which_break_the_protocol()
    {
        ObjectId commitId = _scope.Commit("file", "text");
        using CatFileSession session = _scope.StartSession();

        foreach (string name in (string[])["a\nb", "a\rb", "a\0b", "a\uD800b"])
        {
            session.GetInfo($"{commitId}:{name}", CancellationToken.None).Should().BeNull();
        }

        session.IsStopped.Should().BeFalse();
        session.GetInfo($"{commitId}:file", CancellationToken.None).Should().NotBeNull();
    }

    [Test]
    public void Session_should_find_objects_created_later()
    {
        ObjectId commitId = _scope.Commit("file", "text");
        using CatFileSession session = _scope.StartSession();
        session.GetInfo($"{commitId}:file", CancellationToken.None).Should().NotBeNull();

        ObjectId looseCommitId = _scope.Commit("loose", "loose");
        session.GetInfo($"{looseCommitId}:loose", CancellationToken.None).Should().NotBeNull();

        ObjectId packedCommitId = _scope.Commit("packed", "packed");
        _scope.Git("repack -adq");
        session.GetInfo($"{packedCommitId}:packed", CancellationToken.None).Should().NotBeNull();
        session.GetInfo($"{commitId}:file", CancellationToken.None).Should().NotBeNull();
    }

    [Test]
    public void Cancelled_request_should_stop_the_session()
    {
        using CancellationTokenSource cancellation = new();
        using BlockingProcess process = new(response: $"{Id1} blob 10\nab", atEnd: cancellation.Cancel);
        using CatFileSession session = new(process);

        Action request = () => session.GetBlob(ObjectId.Parse(Id1), cancellation.Token);

        request.Should().Throw<OperationCanceledException>();
        session.IsStopped.Should().BeTrue();
        Action second = () => session.GetBlob(ObjectId.Parse(Id1), CancellationToken.None);
        second.Should().Throw<GitBrokerException>("the remaining part of the first response must not be read");
    }

    [Test]
    public void Cancelled_request_should_not_stop_the_session_if_the_response_is_complete_soon()
    {
        using CancellationTokenSource cancellation = new();
        using BlockingProcess process = new($"{Id1} blob 10\n", thenEach: [cancellation.Cancel, null], responses: ["0123456789\n", $"{Id1} blob 2\nab\n"]);
        using CatFileSession session = new(process);

        Action request = () => session.GetBlob(ObjectId.Parse(Id1), cancellation.Token);

        request.Should().Throw<OperationCanceledException>();
        session.IsStopped.Should().BeFalse();
        session.GetBlob(ObjectId.Parse(Id1), CancellationToken.None).Should().Equal("ab"u8.ToArray(), "the first response must have been read");
    }

    [Test]
    public void Cancelled_request_should_stop_the_session_if_the_response_is_large()
    {
        using CancellationTokenSource cancellation = new();
        using BlockingProcess process = new(thenEach: [cancellation.Cancel], responses: [$"{Id1} blob 10000000\n"]);
        using CatFileSession session = new(process);

        Action request = () => session.GetBlob(ObjectId.Parse(Id1), cancellation.Token);

        request.Should().Throw<OperationCanceledException>();
        session.IsStopped.Should().BeTrue();
        process.ReadCountAtEnd.Should().Be(0, "the content must not be waited for");
    }

    [Test]
    public void Missing_object_should_not_be_fetched()
    {
        _scope.Commit("file", "text");
        string marker = _scope.IncludePromisorRemote();
        using (CatFileSession session = _scope.StartSession())
        {
            session.GetInfo($"{Id1}:file", CancellationToken.None).Should().BeNull();
            session.GetBlob(ObjectId.Parse(Id1), CancellationToken.None).Should().BeNull();
            session.IsStopped.Should().BeFalse();
        }

        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process must end");
        File.Exists(marker).Should().BeFalse("the command of the config must not be run");

        // The check of the test: other git processes run it
        _scope.Module.GitExecutable.Execute($"cat-file -t {Id1}", throwOnErrorExit: false).ExitedSuccessfully.Should().BeFalse();
        File.Exists(marker).Should().BeTrue();
    }

    [Test]
    public void Command_line_should_be_known_as_a_persistent_process()
    {
        CommandLog.Clear();
        using CatFileSession session = _scope.StartSession();
        session.GetInfo("HEAD", CancellationToken.None);

        CommandLogEntry command = CommandLog.Commands.Should().ContainSingle().Subject;
        IndexRefreshGate.IsPersistentProcess($"\"C:\\Program Files\\Git\\git.exe\" {command.Arguments}").Should().BeTrue();
        using Process process = Process.GetProcessById(command.ProcessId!.Value);
        IndexRefreshGate.IsPersistentProcess(process.TryGetCommandLine()).Should().BeTrue();
    }

    [Test]
    public void Request_cancelled_before_should_not_stop_the_session()
    {
        ObjectId blobId = _scope.AddBlob("text"u8.ToArray());
        using CatFileSession session = _scope.StartSession();

        Action request = () => session.GetBlob(blobId, new CancellationToken(canceled: true));

        request.Should().Throw<OperationCanceledException>();
        session.IsStopped.Should().BeFalse();
        session.GetBlob(blobId, CancellationToken.None).Should().Equal("text"u8.ToArray());
    }

    [TestCase("")]
    [TestCase("unexpected\n")]
    [TestCase(Id1)]
    [TestCase($"{Id1} blob 2\nab")]
    [TestCase($"{Id1} blob 2\nabc\n")]
    [TestCase($"{Id1} blob -1\n")]
    [TestCase($"{Id1} blob 99999999999999999999\n")]
    [TestCase("3333333333333333333333333333333333333333 blob 2\nab\n")]
    [TestCase("3333333333333333333333333333333333333333 missing\n")]
    public void Unexpected_response_should_stop_the_session(string response)
    {
        using BlockingProcess process = new(response, endsAfterResponse: true);
        using CatFileSession session = new(process);

        Action request = () => session.GetBlob(ObjectId.Parse(Id1), CancellationToken.None);

        request.Should().Throw<GitBrokerException>();
        session.IsStopped.Should().BeTrue();
    }

    [Test]
    public void Too_large_blob_should_not_be_read()
    {
        using BlockingProcess process = new($"{Id1} blob 999999999999\nab");
        using CatFileSession session = new(process);

        session.GetBlob(ObjectId.Parse(Id1), CancellationToken.None).Should().BeNull();

        session.IsStopped.Should().BeTrue("the content remains in the stream");
    }

    [Test]
    public void Request_should_time_out()
    {
        using BlockingProcess process = new();
        using CatFileSession session = new(process, firstResponseTimeout: TimeSpan.FromMilliseconds(100));

        Action request = () => session.GetInfo("HEAD", CancellationToken.None);

        request.Should().Throw<GitBrokerException>();
        session.IsStopped.Should().BeTrue();
    }

    [Test]
    public void Dispose_should_end_the_process()
    {
        CatFileSession session = _scope.StartSession();
        session.GetInfo("HEAD", CancellationToken.None);
        GitBrokerStats.RunningProcesses.Should().Be(1);

        session.Dispose();

        session.IsStopped.Should().BeTrue();
        GitBrokerTestScope.WaitUntil(() => GitBrokerStats.RunningProcesses == 0, "the process must end");
    }

    [Test]
    public void Session_should_not_lock_the_folder_of_the_repository()
    {
        ObjectId commitId = _scope.Commit("file", "text");
        _scope.Git("repack -adq");
        using CatFileSession session = _scope.StartSession();
        session.GetInfo($"{commitId}:file", CancellationToken.None).Should().NotBeNull();
        string workingDir = _scope.Module.WorkingDir.TrimEnd('\\');

        // E.g. a worktree which is removed in a terminal
        Directory.Move(workingDir, $"{workingDir}-moved");
        Directory.Move($"{workingDir}-moved", workingDir);
    }

    /// <summary>
    ///  Provides a response and then blocks until it is killed.
    /// </summary>
    private sealed class BlockingProcess : IProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _killed = new();

        public BlockingProcess(string response = "", bool endsAfterResponse = false, Action? atEnd = null, Action?[]? thenEach = null, string[]? responses = null)
        {
            StandardInput = new StreamWriter(new MemoryStream());
            StandardOutput = new StreamReader(new ResponseStream(this, Encoding.ASCII.GetBytes(response), endsAfterResponse, atEnd, thenEach ?? [], responses ?? []));
        }

        public int ReadCountAtEnd { get; private set; }

        public StreamWriter StandardInput { get; }

        public StreamReader StandardOutput { get; }

        public string StandardError => "";

        public void Dispose() => Kill();

        public void Kill(bool entireProcessTree = false)
        {
            _exit.TrySetResult(-1);
            _killed.Set();
        }

        public int WaitForExit() => throw new NotSupportedException();

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        public Task<int> WaitForExitAsync() => _exit.Task;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

        public Task<int> WaitForExitAsync(CancellationToken token) => _exit.Task.WaitAsync(token);

        public void WaitForInputIdle() => throw new NotSupportedException();

        private sealed class ResponseStream(BlockingProcess process, byte[] response, bool endsAfterResponse, Action? atEnd, Action?[] thenEach, string[] responses) : Stream
        {
            private byte[] _current = response;
            private int _position;
            private int _next;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position == _current.Length && _next < responses.Length)
                {
                    // The following responses arrive one after the other
                    thenEach[_next]?.Invoke();
                    _current = Encoding.ASCII.GetBytes(responses[_next++]);
                    _position = 0;
                }

                int length = Math.Min(count, _current.Length - _position);
                Array.Copy(_current, _position, buffer, offset, length);
                _position += length;
                if (length == 0 && !endsAfterResponse)
                {
                    process.ReadCountAtEnd++;
                    atEnd?.Invoke();
                    process._killed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the process must be killed");
                }

                return length;
            }
        }
    }
}
