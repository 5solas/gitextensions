using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using CommonTestUtils;
using GitCommands;
using GitCommands.Logging;
using GitCommands.RepoSnapshot;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUI;
using GitUI.CommandsDialogs;
using NUnit.Framework;

namespace GitExtensions.UITests.Performance;

/// <summary>
///  Measures switching back to a repository (A -> B -> A) in an open <see cref="FormBrowse"/>. The repositories are only read.
/// </summary>
/// <remarks>
///  Ignored unless the following environment variables are set:
///  <list type="bullet">
///   <item><c>HJ_SWITCH_PERF_REPO</c>: the repository A</item>
///   <item><c>HJ_SWITCH_PERF_OTHER_REPO</c>: the repository B</item>
///   <item><c>HJ_SWITCH_PERF_OUTPUT</c>: the csv file to create</item>
///   <item><c>HJ_SWITCH_PERF_ITERATIONS</c> (optional)</item>
///   <item><c>HJ_SWITCH_PERF_GC</c> (optional): collect the garbage before each switch to A</item>
///   <item><c>HJ_SWITCH_PERF_LIMIT</c> (optional): <see cref="AppSettings.RepoSnapshotMemoryLimit"/>, 0 to measure without reuse</item>
///  </list>
/// </remarks>
[TestFixture]
[Category("Perf")]
[Apartment(ApartmentState.STA)]
public sealed class RepositorySwitchPerfTests
{
    private const int DefaultIterations = 8;
    private const int StepTimeoutMilliseconds = 60_000;
    private const int IdleSettleMilliseconds = 1_000;
    private const int MaxArgumentsLength = 100;

    private static readonly PropertyInfo? _startedAtProperty
        = typeof(CommandLogEntry).GetProperty("StartedAt", BindingFlags.Instance | BindingFlags.NonPublic);

    [Test]
    public void SwitchRepository_should_measure_time_until_grid_is_loaded()
    {
        string? workingDirA = Environment.GetEnvironmentVariable("HJ_SWITCH_PERF_REPO");
        string? workingDirB = Environment.GetEnvironmentVariable("HJ_SWITCH_PERF_OTHER_REPO");
        string? outputFile = Environment.GetEnvironmentVariable("HJ_SWITCH_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(workingDirA) || string.IsNullOrWhiteSpace(workingDirB) || string.IsNullOrWhiteSpace(outputFile))
        {
            Assert.Ignore("HJ_SWITCH_PERF_REPO, HJ_SWITCH_PERF_OTHER_REPO or HJ_SWITCH_PERF_OUTPUT is not set.");
            return;
        }

        int iterations = int.TryParse(Environment.GetEnvironmentVariable("HJ_SWITCH_PERF_ITERATIONS"), out int parsed) ? parsed : DefaultIterations;
        bool collectGarbage = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HJ_SWITCH_PERF_GC"));
        bool originalLogCaptureCallStacks = AppSettings.LogCaptureCallStacks;
        bool originalShowAuthorAvatarColumn = AppSettings.ShowAuthorAvatarColumn;

        // The stored value, null if the setting is not set: the settings file must not get an entry
        string memoryLimitName = AppSettings.RepoSnapshotMemoryLimit.FullPath;
        string? originalMemoryLimit = AppSettings.GetString(memoryLimitName, defaultValue: null);

        IServiceContainer serviceContainer = GlobalServiceContainer.CreateDefaultMockServiceContainer();
        GitModule moduleB = new(serviceContainer.GetRequiredService<IGitExecutorProvider>(), workingDirB);
        GitUICommands commands = new(serviceContainer, moduleB);

        // Like the start of the application: the first git process must not be started by several threads at the same time
        // (the default encoding in ExecutableExtensions is not thread-safe)
        moduleB.GitExecutable.GetOutput("--version").Should().StartWith("git version");

        List<SwitchResult> results = [];
        using UITestThreadExceptions threadExceptions = new();

        RepoSnapshotStore.Default.Clear();

        try
        {
            AppSettings.ShowAuthorAvatarColumn = false;
            if (int.TryParse(Environment.GetEnvironmentVariable("HJ_SWITCH_PERF_LIMIT"), out int memoryLimit))
            {
                AppSettings.RepoSnapshotMemoryLimit.Value = memoryLimit;
            }

            // The call stacks show which commands are run on the UI thread
            AppSettings.LogCaptureCallStacks = true;

            UITest.RunForm(
                showForm: () => commands.StartBrowseDialog(owner: null).Should().BeTrue(),
                async (FormBrowse form) =>
                {
                    await WaitUntilIdleAsync(form);

                    // Iteration 0 is the first switch to A, without any data of A in memory
                    for (int iteration = 0; iteration <= iterations; iteration++)
                    {
                        if (collectGarbage)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                        }

                        results.Add(await MeasureSwitchAsync(form, workingDirA, iteration));

                        form.SetWorkingDir(workingDirB);
                        await WaitUntilIdleAsync(form);
                    }
                });
        }
        finally
        {
            AppSettings.LogCaptureCallStacks = originalLogCaptureCallStacks;
            AppSettings.ShowAuthorAvatarColumn = originalShowAuthorAvatarColumn;
            AppSettings.SetString(memoryLimitName, originalMemoryLimit!);
            RepoSnapshotStore.Default.Clear();
        }

        threadExceptions.ThrowIfAny();
        Export(outputFile, results);
    }

    private static async Task<SwitchResult> MeasureSwitchAsync(FormBrowse form, string workingDir, int iteration)
    {
        RevisionGridControl revisionGrid = form.GetTestAccessor().RevisionGrid;
        SwitchResult result = new() { Iteration = iteration };
        Stopwatch stopwatch = new();

        revisionGrid.RevisionsLoaded += OnRevisionsLoaded;
        try
        {
            CommandLog.Clear();
            DateTime startedAt = DateTime.Now;
            stopwatch.Start();
            form.SetWorkingDir(workingDir);
            result.SetWorkingDirMs = stopwatch.Elapsed.TotalMilliseconds;

            while (result.RevisionsLoadedMs == 0 && stopwatch.ElapsedMilliseconds < StepTimeoutMilliseconds)
            {
                if (result.FirstRevisionsMs == 0 && revisionGrid.GetTestAccessor().VisibleRevisionCount > 0)
                {
                    result.FirstRevisionsMs = stopwatch.Elapsed.TotalMilliseconds;
                }

                Application.DoEvents();
                Thread.Sleep(1);
            }

            if (result.FirstRevisionsMs == 0)
            {
                result.FirstRevisionsMs = result.RevisionsLoadedMs;
            }

            await WaitUntilIdleAsync(form);
            result.RevisionCount = revisionGrid.GetTestAccessor().VisibleRevisionCount;
            foreach (CommandLogEntry entry in CommandLog.Commands)
            {
                string arguments = CommandLogEntry.GetGitArgumentsWithoutConfiguration(entry.Arguments);
                DateTime commandStartedAt = (DateTime?)_startedAtProperty?.GetValue(entry) ?? startedAt;
                result.Commands.Add(new CommandResult(
                    arguments.Length > MaxArgumentsLength ? arguments[..MaxArgumentsLength] : arguments,
                    (commandStartedAt - startedAt).TotalMilliseconds,
                    entry.Duration?.TotalMilliseconds ?? -1,
                    entry.IsOnMainThread,
                    entry.IsOnMainThread ? entry.CallStack?.ToString() : null));
            }
        }
        finally
        {
            revisionGrid.RevisionsLoaded -= OnRevisionsLoaded;
        }

        return result;

        void OnRevisionsLoaded(object? sender, EventArgs e) => result.RevisionsLoadedMs = stopwatch.Elapsed.TotalMilliseconds;
    }

    private static async Task WaitUntilIdleAsync(FormBrowse form)
    {
        UITest.ProcessUntil(
            "Revision grid loaded",
            () => form.GetTestAccessor().RevisionGrid.GetTestAccessor() is { IsDataLoadComplete: true, IsRefreshingRevisions: false },
            maxMilliseconds: StepTimeoutMilliseconds);
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));
        UITest.ProcessEventsFor(IdleSettleMilliseconds);
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));
    }

    private static void Export(string outputFile, List<SwitchResult> results)
    {
        StringBuilder csv = new();
        csv.AppendLine("Iteration,Kind,Arguments,StartMs,DurationMs,IsOnMainThread,Processes,Revisions");
        foreach (SwitchResult result in results)
        {
            AppendTime("SetWorkingDir", result.SetWorkingDirMs);
            AppendTime("FirstRevisions", result.FirstRevisionsMs);
            AppendTime("RevisionsLoaded", result.RevisionsLoadedMs);
            foreach (CommandResult command in result.Commands)
            {
                csv.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{result.Iteration},git,\"{command.Arguments.Replace("\"", "\"\"")}\",{command.StartMs:F1},{command.DurationMs:F1},{command.IsOnMainThread},,"));
                if (command.CallStack is not null)
                {
                    Console.WriteLine($"PERF {result.Iteration} UI thread: {command.Arguments}\n{command.CallStack}");
                }
            }

            void AppendTime(string kind, double milliseconds)
                => csv.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{result.Iteration},{kind},,0,{milliseconds:F1},,{result.Commands.Count},{result.RevisionCount}"));
        }

        File.WriteAllText(outputFile, csv.ToString());
    }

    private sealed record CommandResult(string Arguments, double StartMs, double DurationMs, bool IsOnMainThread, string? CallStack);

    private sealed class SwitchResult
    {
        public int Iteration { get; init; }

        public double SetWorkingDirMs { get; set; }

        public double FirstRevisionsMs { get; set; }

        public double RevisionsLoadedMs { get; set; }

        public int RevisionCount { get; set; }

        public List<CommandResult> Commands { get; } = [];
    }
}
