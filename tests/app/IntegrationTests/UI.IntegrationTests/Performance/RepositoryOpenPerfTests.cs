using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using CommonTestUtils;
using GitCommands;
using GitCommands.Logging;
using GitCommands.Settings;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtensions.UITests;
using GitExtUtils;
using GitUI;
using GitUI.CommandsDialogs;
using GitUI.CommitInfo;
using GitUI.LeftPanel;
using GitUI.UserControls;
using GitUI.UserControls.RevisionGrid;
using NUnit.Framework;

namespace GitExtensions.UITests.Performance;

[TestFixture]
[Category("Perf")]
[Apartment(ApartmentState.STA)]
public sealed class RepositoryOpenPerfTests
{
    [Test]
    public void OpenRepository_should_measure_and_export_performance_metrics()
    {
        string? repoEnv = Environment.GetEnvironmentVariable("HJ_PERF_REPO");
        if (string.IsNullOrWhiteSpace(repoEnv))
        {
            Assert.Ignore("HJ_PERF_REPO environment variable is not set.");
            return;
        }

        string[] repoPaths = repoEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (repoPaths.Length == 0)
        {
            Assert.Ignore("No valid repository paths found in HJ_PERF_REPO.");
            return;
        }

        int iterations = 5;
        if (int.TryParse(Environment.GetEnvironmentVariable("HJ_PERF_ITERATIONS"), out int parsedIterations) && parsedIterations > 0)
        {
            iterations = parsedIterations;
        }

        string worktreeRoot = GetWorktreeRoot();
        string? outputDirEnv = Environment.GetEnvironmentVariable("HJ_PERF_OUTPUT_DIR");
        string outputDir = string.IsNullOrWhiteSpace(outputDirEnv)
            ? Path.Combine(worktreeRoot, "docs", "hj", "perf")
            : Path.GetFullPath(outputDirEnv);
        Directory.CreateDirectory(outputDir);

        bool originalCaptureCallStacks = AppSettings.LogCaptureCallStacks;
        bool originalShowStashes = AppSettings.ShowStashes;
        bool originalShowAheadBehind = AppSettings.ShowAheadBehindData;

        try
        {
            AppSettings.LogCaptureCallStacks = true;

            foreach (string repoPath in repoPaths)
            {
                if (!Directory.Exists(repoPath))
                {
                    TestContext.Out.WriteLine($"Skipping non-existent repository: {repoPath}");
                    continue;
                }

                string repoName = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                TestContext.Out.WriteLine($"=== Measuring Repository: {repoName} ({repoPath}) ===");

                List<RunResult> results = [];

                // 1. Regular iterations (Run 1 = Cold, Run 2..N = Warm)
                AppSettings.ShowStashes = true;
                AppSettings.ShowAheadBehindData = true;

                for (int i = 1; i <= iterations; i++)
                {
                    string runName = i == 1 ? "Run1_Cold" : $"Run{i}_Warm";
                    TestContext.Out.WriteLine($"  Executing {runName}...");

                    RunResult result = MeasureSingleRun(repoPath, runName);
                    results.Add(result);

                    string runCsvPath = Path.Combine(outputDir, $"{repoName}-run{i}.csv");
                    ExportRunCsv(runCsvPath, result.Entries);
                    TestContext.Out.WriteLine($"    Saved {runCsvPath} ({result.TotalProcessCount} git processes, {result.TotalWallClockMs} ms wall clock)");
                }

                // 2. Sensitivity test: ShowStashes = false
                TestContext.Out.WriteLine("  Executing sensitivity test: ShowStashes=false...");
                AppSettings.ShowStashes = false;
                AppSettings.ShowAheadBehindData = true;
                RunResult noStashesResult = MeasureSingleRun(repoPath, "NoStashes");
                results.Add(noStashesResult);
                string noStashesCsvPath = Path.Combine(outputDir, $"{repoName}-no-stashes.csv");
                ExportRunCsv(noStashesCsvPath, noStashesResult.Entries);
                TestContext.Out.WriteLine($"    Saved {noStashesCsvPath} ({noStashesResult.TotalProcessCount} git processes, {noStashesResult.TotalWallClockMs} ms wall clock)");

                // 3. Sensitivity test: ShowAheadBehind = false
                TestContext.Out.WriteLine("  Executing sensitivity test: ShowAheadBehind=false...");
                AppSettings.ShowStashes = true;
                AppSettings.ShowAheadBehindData = false;
                RunResult noAheadBehindResult = MeasureSingleRun(repoPath, "NoAheadBehind");
                results.Add(noAheadBehindResult);
                string noAheadBehindCsvPath = Path.Combine(outputDir, $"{repoName}-no-ahead-behind.csv");
                ExportRunCsv(noAheadBehindCsvPath, noAheadBehindResult.Entries);
                TestContext.Out.WriteLine($"    Saved {noAheadBehindCsvPath} ({noAheadBehindResult.TotalProcessCount} git processes, {noAheadBehindResult.TotalWallClockMs} ms wall clock)");

                // Export summary CSV
                string summaryCsvPath = Path.Combine(outputDir, $"{repoName}-summary.csv");
                ExportSummaryCsv(summaryCsvPath, results);
                TestContext.Out.WriteLine($"  Saved {summaryCsvPath}");
            }
        }
        finally
        {
            AppSettings.LogCaptureCallStacks = originalCaptureCallStacks;
            AppSettings.ShowStashes = originalShowStashes;
            AppSettings.ShowAheadBehindData = originalShowAheadBehind;
        }
    }

    private static RunResult MeasureSingleRun(string repoPath, string runName)
    {
        CommandLog.Clear();
        RunResult result = new() { RunName = runName };
        Stopwatch sw = new();
        DateTime runStartTime = DateTime.Now;

        IServiceContainer serviceContainer = GlobalServiceContainer.CreateDefaultMockServiceContainer();
        IGitExecutorProvider executorProvider = serviceContainer.GetRequiredService<IGitExecutorProvider>();
        GitUICommands commands = new(serviceContainer, new GitModule(executorProvider, repoPath));

        long revisionsLoadingMs = -1;
        long firstBatchMs = -1;
        long revisionsLoadedMs = -1;
        long leftPanelCompletedMs = -1;
        long diffListCompletedMs = -1;
        long commitInfoCompletedMs = -1;

        UITest.RunForm<FormBrowse>(
            showForm: () =>
            {
                sw.Start();
                runStartTime = DateTime.Now;
                commands.StartBrowseDialog(owner: null).Should().BeTrue();
            },
            runTestAsync: async form =>
            {
                // RevisionsLoading might already have started
                revisionsLoadingMs = 0;

                // 1. Wait for revisions loaded / first batch
                bool revisionsLoaded = await WaitStepAsync(
                    "revisions loaded",
                    () =>
                    {
                        if (firstBatchMs < 0 && form.GetTestAccessor().RevisionGrid.GetTestAccessor().VisibleRevisionCount > 0)
                        {
                            firstBatchMs = sw.ElapsedMilliseconds;
                        }

                        if (form.GetTestAccessor().RevisionGrid.GetTestAccessor().IsDataLoadComplete)
                        {
                            if (revisionsLoadedMs < 0)
                            {
                                revisionsLoadedMs = sw.ElapsedMilliseconds;
                            }

                            if (firstBatchMs < 0)
                            {
                                firstBatchMs = revisionsLoadedMs;
                            }

                            return true;
                        }

                        return false;
                    },
                    30_000);

                if (revisionsLoaded && revisionsLoadedMs < 0)
                {
                    revisionsLoadedMs = sw.ElapsedMilliseconds;
                }

                // 2. Wait for left panel
                bool leftPanelLoaded = await WaitStepAsync(
                    "left panel",
                    () => IsLeftPanelLoaded(form.GetTestAccessor().RepoObjectsTree),
                    15_000);

                if (leftPanelLoaded)
                {
                    leftPanelCompletedMs = sw.ElapsedMilliseconds;
                }

                // 3. Select HEAD and activate DiffTabPage
                form.GetTestAccessor().CommitInfoTabControl.SelectedTab = form.GetTestAccessor().DiffTabPage;
                ObjectId head = form.GetTestAccessor().RevisionGrid.CurrentCheckout;
                if (!head.IsZero)
                {
                    form.GetTestAccessor().RevisionGrid.SetSelectedRevision(head);
                }

                // 4. Wait for diff list
                bool diffLoaded = await WaitStepAsync(
                    "diff list",
                    () =>
                    {
                        RevisionDiffControl? diff = form.GetTestAccessor().RevisionDiffControl;
                        if (diff is null)
                        {
                            return false;
                        }

                        FileStatusList? diffFiles = diff.GetTestAccessor().DiffFiles;
                        return diffFiles?.GitItemStatuses is not null && diffFiles.GitItemStatuses.Count > 0;
                    },
                    15_000);

                if (diffLoaded)
                {
                    diffListCompletedMs = sw.ElapsedMilliseconds;
                }

                // 5. Activate CommitInfoTabPage and wait for commit info (max 2 seconds, then process events)
                form.GetTestAccessor().CommitInfoTabControl.SelectedTab = form.GetTestAccessor().CommitInfoTabPage;
                bool commitInfoLoaded = await WaitStepAsync(
                    "commit info",
                    () => IsCommitInfoLoaded(form.GetTestAccessor().RevisionInfo),
                    2_000);

                if (commitInfoLoaded)
                {
                    commitInfoCompletedMs = sw.ElapsedMilliseconds;
                }
                else
                {
                    // RevisionsLoaded 후 2초 유휴
                    WinFormsTestHelper.ProcessEventsFor(2000);
                    commitInfoCompletedMs = sw.ElapsedMilliseconds;
                }

                sw.Stop();
            });

        result.RevisionsLoadingMs = revisionsLoadingMs;
        result.FirstBatchMs = firstBatchMs;
        result.RevisionsLoadedMs = revisionsLoadedMs;
        result.LeftPanelCompletedMs = leftPanelCompletedMs;
        result.DiffListCompletedMs = diffListCompletedMs;
        result.CommitInfoCompletedMs = commitInfoCompletedMs;
        result.TotalWallClockMs = sw.ElapsedMilliseconds;

        CommandLogEntry[] commandsSnapshot = CommandLog.Commands.ToArray();
        result.TotalProcessCount = commandsSnapshot.Length;
        result.UIThreadProcessCount = commandsSnapshot.Count(c => c.IsOnMainThread);
        result.UIThreadDurationMs = commandsSnapshot
            .Where(c => c.IsOnMainThread && c.Duration is not null)
            .Sum(c => c.Duration!.Value.TotalMilliseconds);

        PropertyInfo? startedAtProp = typeof(CommandLogEntry).GetProperty("StartedAt", BindingFlags.Instance | BindingFlags.NonPublic);

        int index = 1;
        foreach (CommandLogEntry entry in commandsSnapshot)
        {
            DateTime startedAt = (DateTime?)startedAtProp?.GetValue(entry) ?? runStartTime;
            double startOffsetMs = (startedAt - runStartTime).TotalMilliseconds;
            if (startOffsetMs < 0)
            {
                startOffsetMs = 0;
            }

            double durationMs = entry.Duration?.TotalMilliseconds ?? 0;
            string fullCmd = entry.CommandLine;
            string truncatedCmd = fullCmd.Length > 200 ? fullCmd[..200] : fullCmd;
            string callStackStr = FormatCallStackTop3(entry.CallStack);

            result.Entries.Add(new CommandLogEntryRecord
            {
                Index = index++,
                StartOffsetMs = Math.Round(startOffsetMs, 1),
                DurationMs = Math.Round(durationMs, 1),
                IsOnMainThread = entry.IsOnMainThread,
                ExitCode = entry.ExitCode,
                CommandLine = truncatedCmd,
                CallStack = callStackStr
            });
        }

        return result;
    }

    private static async Task<bool> WaitStepAsync(string stepName, Func<bool> condition, int maxMilliseconds)
    {
        const int delayMs = 25;
        int maxIterations = (maxMilliseconds + delayMs - 1) / delayMs;

        for (int i = 0; i < maxIterations; i++)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                await TestContext.Out.WriteLineAsync($"  [Notice] Exception in '{stepName}': {ex.Message}");
            }

            Application.DoEvents();
            await Task.Delay(delayMs);
        }

        await TestContext.Out.WriteLineAsync($"  [Timeout] '{stepName}' timed out after {maxMilliseconds}ms");
        return false;
    }

    private static string GetWorktreeRoot()
    {
        string current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new InvalidOperationException($"Could not locate the repository root above '{AppDomain.CurrentDomain.BaseDirectory}'.");
    }

    private static string FormatCallStackTop3(StackTrace? stackTrace)
    {
        if (stackTrace is null)
        {
            return string.Empty;
        }

        StackFrame[]? frames = stackTrace.GetFrames();
        if (frames is null || frames.Length == 0)
        {
            return string.Empty;
        }

        List<string> frameStrings = [];
        foreach (StackFrame frame in frames)
        {
            MethodBase? method = frame.GetMethod();
            if (method is null)
            {
                continue;
            }

            string typeName = method.DeclaringType?.Name ?? "Unknown";
            if (typeName.Contains("CommandLog") || typeName.Contains("ProcessOperation"))
            {
                continue;
            }

            string methodName = method.Name;
            int line = frame.GetFileLineNumber();
            frameStrings.Add(line > 0 ? $"{typeName}.{methodName}:{line}" : $"{typeName}.{methodName}");

            if (frameStrings.Count >= 3)
            {
                break;
            }
        }

        return string.Join(" -> ", frameStrings);
    }

    private static void ExportRunCsv(string filePath, List<CommandLogEntryRecord> entries)
    {
        StringBuilder sb = new();
        sb.AppendLine("Index,StartOffsetMs,DurationMs,IsOnMainThread,ExitCode,CommandLine,CallStack");
        foreach (CommandLogEntryRecord e in entries)
        {
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1:F1},{2:F1},{3},{4},{5},{6}",
                e.Index,
                e.StartOffsetMs,
                e.DurationMs,
                e.IsOnMainThread,
                e.ExitCode.HasValue ? e.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                EscapeCsv(e.CommandLine),
                EscapeCsv(e.CallStack)));
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static void ExportSummaryCsv(string filePath, List<RunResult> results)
    {
        StringBuilder sb = new();
        sb.AppendLine("Run,RevisionsLoadingMs,FirstBatchMs,RevisionsLoadedMs,LeftPanelCompletedMs,DiffListCompletedMs,CommitInfoCompletedMs,TotalWallClockMs,TotalProcessCount,UIThreadProcessCount,UIThreadDurationMs");
        foreach (RunResult r in results)
        {
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10:F1}",
                r.RunName,
                r.RevisionsLoadingMs >= 0 ? r.RevisionsLoadingMs.ToString() : "timeout",
                r.FirstBatchMs >= 0 ? r.FirstBatchMs.ToString() : "timeout",
                r.RevisionsLoadedMs >= 0 ? r.RevisionsLoadedMs.ToString() : "timeout",
                r.LeftPanelCompletedMs >= 0 ? r.LeftPanelCompletedMs.ToString() : "timeout",
                r.DiffListCompletedMs >= 0 ? r.DiffListCompletedMs.ToString() : "timeout",
                r.CommitInfoCompletedMs >= 0 ? r.CommitInfoCompletedMs.ToString() : "timeout",
                r.TotalWallClockMs,
                r.TotalProcessCount,
                r.UIThreadProcessCount,
                r.UIThreadDurationMs));
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private static bool IsLeftPanelLoaded(RepoObjectsTree repoObjectsTree)
    {
        if (repoObjectsTree is null)
        {
            return false;
        }

        FieldInfo? branchesField = typeof(RepoObjectsTree).GetField("_branchesTree", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? remotesField = typeof(RepoObjectsTree).GetField("_remotesTree", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? tagField = typeof(RepoObjectsTree).GetField("_tagTree", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? worktreeField = typeof(RepoObjectsTree).GetField("_worktreeTree", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? stashField = typeof(RepoObjectsTree).GetField("_stashTree", BindingFlags.Instance | BindingFlags.NonPublic);

        Tree? branchesTree = branchesField?.GetValue(repoObjectsTree) as Tree;
        Tree? remotesTree = remotesField?.GetValue(repoObjectsTree) as Tree;
        Tree? tagTree = tagField?.GetValue(repoObjectsTree) as Tree;
        Tree? worktreeTree = worktreeField?.GetValue(repoObjectsTree) as Tree;
        Tree? stashTree = stashField?.GetValue(repoObjectsTree) as Tree;

        return IsTreeLoaded(branchesTree)
            && IsTreeLoaded(remotesTree)
            && IsTreeLoaded(tagTree)
            && IsTreeLoaded(worktreeTree)
            && IsTreeLoaded(stashTree);
    }

    private static bool IsTreeLoaded(Tree? tree)
    {
        if (tree is null)
        {
            return true;
        }

        PropertyInfo? isAttachedProp = typeof(Tree).GetProperty("IsAttached", BindingFlags.Instance | BindingFlags.NonPublic);
        bool isAttached = (bool?)isAttachedProp?.GetValue(tree) ?? false;
        if (!isAttached)
        {
            return true;
        }

        FieldInfo? tcsField = typeof(Tree).GetField("LoadingCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (tcsField?.GetValue(tree) is TaskCompletionSource tcs)
        {
            return tcs.Task.IsCompleted;
        }

        return true;
    }

    private static bool IsCommitInfoLoaded(CommitInfo commitInfo)
    {
        if (commitInfo is null)
        {
            return false;
        }

        RichTextBox messageBox = commitInfo.GetTestAccessor().CommitMessage;
        return messageBox is not null && !string.IsNullOrWhiteSpace(messageBox.Text);
    }

    private sealed class RunResult
    {
        public string RunName { get; set; } = string.Empty;
        public long RevisionsLoadingMs { get; set; } = -1;
        public long FirstBatchMs { get; set; } = -1;
        public long RevisionsLoadedMs { get; set; } = -1;
        public long LeftPanelCompletedMs { get; set; } = -1;
        public long DiffListCompletedMs { get; set; } = -1;
        public long CommitInfoCompletedMs { get; set; } = -1;
        public long TotalWallClockMs { get; set; }
        public int TotalProcessCount { get; set; }
        public int UIThreadProcessCount { get; set; }
        public double UIThreadDurationMs { get; set; }
        public List<CommandLogEntryRecord> Entries { get; set; } = [];
    }

    private sealed class CommandLogEntryRecord
    {
        public int Index { get; set; }
        public double StartOffsetMs { get; set; }
        public double DurationMs { get; set; }
        public bool IsOnMainThread { get; set; }
        public int? ExitCode { get; set; }
        public string CommandLine { get; set; } = string.Empty;
        public string CallStack { get; set; } = string.Empty;
    }
}
