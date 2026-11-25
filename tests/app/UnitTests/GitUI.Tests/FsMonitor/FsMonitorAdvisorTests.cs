using GitUI.FsMonitor;

namespace GitUITests.FsMonitor;

public sealed class FsMonitorAdvisorTests
{
    private string _workingDir = null!;
    private string _gitDir = null!;

    [SetUp]
    public void SetUp()
    {
        _workingDir = Path.Combine(Path.GetTempPath(), $"ge-fsmonitor-{Guid.NewGuid():N}");
        _gitDir = Path.Combine(_workingDir, ".git");
        Directory.CreateDirectory(_gitDir);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_workingDir, recursive: true);
    }

    [TestCase(1024 * 1024, 0, FsMonitorAdvice.Recommended)]
    [TestCase(1024 * 1024, 9, FsMonitorAdvice.Recommended)]
    [TestCase(1024 * 1024, 10, FsMonitorAdvice.ManySubmodules)]
    [TestCase((1024 * 1024) - 1, 0, FsMonitorAdvice.SmallRepository)]
    [TestCase(null, 0, FsMonitorAdvice.SmallRepository)]
    public void Evaluate_should_consider_the_size(int? indexBytes, int submodules, object expected)
    {
        if (indexBytes is int size)
        {
            File.WriteAllBytes(Path.Combine(_gitDir, "index"), new byte[size]);
        }

        File.WriteAllLines(Path.Combine(_workingDir, ".gitmodules"), Enumerable.Range(0, submodules).Select(i => $"[submodule \"s{i}\"]\n\tpath = s{i}"));

        FsMonitorAdvisor.Evaluate(_workingDir, _gitDir, isBare: false, _ => DriveType.Fixed).Should().Be(((FsMonitorAdvice)expected, (long)(indexBytes ?? 0)));
    }

    [TestCase(DriveType.Network, FsMonitorAdvice.NetworkDrive)]
    [TestCase(DriveType.Removable, FsMonitorAdvice.NotFixedDrive)]
    [TestCase(DriveType.Unknown, FsMonitorAdvice.NotFixedDrive)]
    public void Evaluate_should_consider_the_drive(DriveType driveType, object expected)
    {
        FsMonitorAdvisor.Evaluate(_workingDir, _gitDir, isBare: false, _ => driveType)!.Value.Advice.Should().Be((FsMonitorAdvice)expected);
    }

    [TestCase(@"\\server\share\repo\")]
    [TestCase(@"\\wsl$\Ubuntu\home\repo\")]
    public void Evaluate_should_consider_network_paths(string workingDir)
    {
        FsMonitorAdvisor.Evaluate(workingDir, workingDir + @".git\", isBare: false, _ => throw new InvalidOperationException())!.Value.Advice.Should().Be(FsMonitorAdvice.NetworkDrive);
    }

    [Test]
    public void Evaluate_should_consider_bare_and_invalid_repositories()
    {
        FsMonitorAdvisor.Evaluate(_workingDir, _gitDir, isBare: true)!.Value.Advice.Should().Be(FsMonitorAdvice.BareRepository);
        FsMonitorAdvisor.Evaluate("", "", isBare: false).Should().BeNull();
        FsMonitorAdvisor.Evaluate(_workingDir, _gitDir, isBare: false, _ => throw new IOException()).Should().BeNull();
    }
}
