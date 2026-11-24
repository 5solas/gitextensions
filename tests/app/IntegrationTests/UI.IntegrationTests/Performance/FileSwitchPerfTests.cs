using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using CommonTestUtils;
using GitCommands;
using GitCommands.Git;
using GitCommands.Git.Broker;
using GitCommands.Logging;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.Editor;
using NUnit.Framework;

namespace GitExtensions.UITests.Performance;

/// <summary>
///  Measures viewing files one after the other with and without the persistent git process. The repository is only read.
/// </summary>
/// <remarks>
///  Ignored unless the following environment variables are set:
///  <list type="bullet">
///   <item><c>HJ_FILE_PERF_REPO</c>: the repository</item>
///   <item><c>HJ_FILE_PERF_OUTPUT</c>: the csv file to create</item>
///   <item><c>HJ_FILE_PERF_FILES</c> (optional): number of files</item>
///  </list>
/// </remarks>
[TestFixture]
[Category("Perf")]
[Apartment(ApartmentState.STA)]
public sealed class FileSwitchPerfTests
{
    private const int DefaultFileCount = 32;
    private const int StepTimeoutMilliseconds = 60_000;

    [Test]
    public void ViewFiles_should_measure_time_until_text_is_shown()
    {
        string? workingDir = Environment.GetEnvironmentVariable("HJ_FILE_PERF_REPO");
        string? outputFile = Environment.GetEnvironmentVariable("HJ_FILE_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(workingDir) || string.IsNullOrWhiteSpace(outputFile))
        {
            Assert.Ignore("HJ_FILE_PERF_REPO or HJ_FILE_PERF_OUTPUT is not set.");
            return;
        }

        int fileCount = int.TryParse(Environment.GetEnvironmentVariable("HJ_FILE_PERF_FILES"), out int parsed) ? parsed : DefaultFileCount;
        string? originalUseGitBroker = AppSettings.GetString(AppSettings.UseGitBroker.FullPath, defaultValue: null);
        GitModule module = new(new GitExecutorProvider(new GitDirectoryResolver()), workingDir);

        // Like the start of the application: the first git process must not be started by several threads at the same time
        module.GitExecutable.GetOutput("--version").Should().StartWith("git version");
        ObjectId commitId = module.GetCurrentCheckout();
        string[] allFiles = [.. module.GetTree(commitId, full: true).Select(item => item.Name).Where(name => name.EndsWith(".cs"))];
        string[] files = [.. allFiles.Where((_, index) => index % (allFiles.Length / fileCount) == 0).Take(fileCount)];

        TestGitUICommandsSource uiCommandsSource = new(new GitUICommands(GlobalServiceContainer.CreateDefaultMockServiceContainer(), module));

        StringBuilder csv = new("File,Mode,Milliseconds,Processes,WorkingSetKB,PrivateKB\r\n");
        using UITestThreadExceptions threadExceptions = new();
        try
        {
            UITest.RunControl(
                createControl: form => new FileViewer { Dock = DockStyle.Fill, Parent = form, UICommandsSource = uiCommandsSource },
                runTestAsync: async fileViewer =>
                {
                    // Not measured: the first view loads the editor. The file is not one of the measured ones.
                    AppSettings.UseGitBroker.Value = false;
                    await ViewAsync(fileViewer, allFiles[1], commitId);

                    int? processId = null;
                    for (int i = 0; i < files.Length; i++)
                    {
                        // Alternating order, each time without any data in memory
                        foreach (bool useBroker in i % 2 == 0 ? [false, true] : (bool[])[true, false])
                        {
                            AppSettings.UseGitBroker.Value = useBroker;
                            ClearCaches();
                            await MeasureAsync(useBroker ? "on" : "off", files[i]);
                            if (useBroker)
                            {
                                processId = CommandLog.Commands.LastOrDefault(c => c.Arguments.EndsWith("--batch-command"))?.ProcessId ?? processId;
                                await MeasureAsync("on again", files[i]);
                            }
                        }
                    }

                    async Task MeasureAsync(string mode, string file)
                    {
                        (double milliseconds, int processes) = await ViewAsync(fileViewer, file, commitId);
                        (long workingSet, long privateMemory) = GetMemory(processId);
                        csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{file},{mode},{milliseconds:F1},{processes},{workingSet / 1024},{privateMemory / 1024}"));
                    }
                });
        }
        finally
        {
            AppSettings.SetString(AppSettings.UseGitBroker.FullPath, originalUseGitBroker!);
            GitBroker.ReleaseAll();
            ClearCaches();
        }

        threadExceptions.ThrowIfAny();
        UITest.ProcessUntil("Persistent processes ended", () => GitBrokerStats.RunningProcesses == 0, StepTimeoutMilliseconds);
        File.WriteAllText(outputFile, csv.ToString());
    }

    private static void ClearCaches()
    {
        GitModule.GitCommandCache.Clear();
        GitBroker.Cache.Clear();
        ((IDictionary)typeof(FileViewer).GetField("_treeCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Clear();
    }

    private static (long WorkingSet, long PrivateMemory) GetMemory(int? processId)
    {
        if (processId is null)
        {
            return (0, 0);
        }

        try
        {
            using Process process = Process.GetProcessById(processId.Value);
            return (process.WorkingSet64, process.PrivateMemorySize64);
        }
        catch (ArgumentException)
        {
            return (0, 0);
        }
    }

    private static async Task<(double Milliseconds, int Processes)> ViewAsync(FileViewer fileViewer, string file, ObjectId commitId)
    {
        double milliseconds = 0;
        Stopwatch stopwatch = new();
        fileViewer.TextLoaded += OnTextLoaded;
        CommandLog.Clear();
        stopwatch.Start();
        _ = fileViewer.ViewGitItemAsync(new GitItemStatus(file) { IsTracked = true }, commitId);
        while (milliseconds == 0 && stopwatch.ElapsedMilliseconds < StepTimeoutMilliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(0);
        }

        fileViewer.TextLoaded -= OnTextLoaded;
        milliseconds.Should().BeGreaterThan(0);
        await AsyncTestHelper.JoinPendingOperationsAsync(TimeSpan.FromMilliseconds(StepTimeoutMilliseconds));
        UITest.ProcessEventsFor(milliseconds: 50);
        return (milliseconds, CommandLog.Commands.Count());

        void OnTextLoaded(object? sender, EventArgs e) => milliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }
}
