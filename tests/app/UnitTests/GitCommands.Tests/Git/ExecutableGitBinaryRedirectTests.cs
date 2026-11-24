using System.Diagnostics;
using System.Reflection;
using System.Text;
using GitCommands;
using GitCommands.Logging;
using GitExtensions.Extensibility;

namespace GitCommandsTests.Git;

/// <summary>
///  Checks the hook in <see cref="Executable"/> which starts the real git binary instead of the launcher.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ExecutableGitBinaryRedirectTests
{
    [Test]
    public void Start_should_run_the_real_git_binary_and_log_the_configured_command()
    {
        string configured = AppSettings.GitCommand;
        GitBinaryResolver.ClearCache();
        string? real = GitBinaryResolver.Resolve(configured);
        if (!AppSettings.UseDirectGitBinary.Value || real is null)
        {
            Assert.Ignore($"The git installation of '{configured}' is not redirected.");
        }

        Executable executable = new(configured, Path.GetTempPath());
        using IProcess process = executable.Start("--version", redirectOutput: true, outputEncoding: Encoding.UTF8);

        Process started = (Process)process.GetType().GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(process)!;
        started.StartInfo.FileName.Should().Be(real);
        process.StandardOutput.ReadToEnd().Should().StartWith("git version");
        process.WaitForExit();

        // The log keeps the configured command, it identifies git commands
        CommandLog.Commands.Last(c => c.Arguments == "--version").FileName.Should().Be(configured);
    }
}
