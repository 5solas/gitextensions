using System;
using AwesomeAssertions;
using GitUI.ConsoleEmulation.Ghostty;

namespace GitUITests.ConsoleEmulation.Ghostty;

[TestFixture]
public sealed class GhosttyShellRunnerTests
{
    [Test]
    public void SplitCommandLine_should_split_quoted_executable_with_multiple_arguments()
    {
        const string commandLine = "\"C:\\Program Files\\Git\\bin\\bash.exe\" --login -i";

        (string app, IReadOnlyList<string> arguments) = GhosttyShellRunner.SplitCommandLine(commandLine);

        app.Should().Be(@"C:\Program Files\Git\bin\bash.exe");
        arguments.Should().Equal(["--login", "-i"]);
    }

    [Test]
    public void SplitCommandLine_should_parse_quoted_executable_with_no_arguments()
    {
        const string commandLine = "\"C:\\x\\pwsh.exe\"";

        (string app, IReadOnlyList<string> arguments) = GhosttyShellRunner.SplitCommandLine(commandLine);

        app.Should().Be(@"C:\x\pwsh.exe");
        arguments.Should().BeEmpty();
    }

    [Test]
    public void SplitCommandLine_should_parse_unquoted_executable_with_quoted_argument()
    {
        const string commandLine = "cmd.exe /k \"echo a b\"";

        (string app, IReadOnlyList<string> arguments) = GhosttyShellRunner.SplitCommandLine(commandLine);

        app.Should().Be("cmd.exe");
        arguments.Should().Equal(["/k", "echo a b"]);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void SplitCommandLine_should_throw_ArgumentException_when_empty_or_whitespace(string? commandLine)
    {
        Action action = () => GhosttyShellRunner.SplitCommandLine(commandLine!);

        action.Should().Throw<ArgumentException>();
    }
}
