using CommonTestUtils;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.LeftPanel;
using NSubstitute;

namespace GitUITests.LeftPanel;

[Apartment(ApartmentState.STA)]
public sealed class WorktreeLazyLoaderTests
{
    private ReferenceRepository _repository = null!;
    private TreeView _treeView = null!;
    private WorktreeTree _tree = null!;
    private WorktreeLazyLoader _loader = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new ReferenceRepository();
        _treeView = new TreeView();
        TreeNode root = _treeView.Nodes.Add("root");
        root.Nodes.Add("child");
        _tree = new WorktreeTree(root, Substitute.For<IGitUICommandsSource>());
        _loader = new WorktreeLazyLoader(_tree);
        _ = _treeView.Handle;
    }

    [TearDown]
    public void TearDown()
    {
        _tree.Dispose();
        _treeView.Dispose();
        _repository.Dispose();
    }

    [Test]
    public void Displayed_nodes_should_be_lazy()
    {
        _loader.OnRefresh();
        Display(_loader.TryLoad(_repository.Module)!);
        _loader.IsLazy.Should().BeTrue();

        _treeView.Nodes[0].Expand();
        _loader.OnRefresh();
        _loader.TryLoad(_repository.Module).Should().BeNull();
        _loader.IsLazy.Should().BeFalse("the full load has been requested");
        Display(new Nodes(_tree));
        _loader.IsLazy.Should().BeFalse();
    }

    [Test]
    public void Superseded_load_should_not_make_the_nodes_lazy()
    {
        using ManualResetEventSlim isLoading = new();
        using ManualResetEventSlim canContinue = new();
        string workingDir = _repository.Module.WorkingDir;
        IGitModule module = Substitute.For<IGitModule>();
        module.WorkingDirGitDir.Returns(_repository.Module.WorkingDirGitDir);
        module.GitCommonDirectory.Returns(_repository.Module.GitCommonDirectory);
        module.WorkingDir.Returns(_ =>
        {
            isLoading.Set();
            canContinue.Wait(TimeSpan.FromSeconds(10));
            return workingDir;
        });

        _loader.OnRefresh();
        Task<Nodes?> superseded = Task.Run(() => _loader.TryLoad(module));
        isLoading.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        // The node is expanded: everything is loaded
        _treeView.Nodes[0].Expand();
        _loader.OnRefresh();
        _loader.TryLoad(_repository.Module).Should().BeNull();
        Display(new Nodes(_tree));

        canContinue.Set();
        ThreadHelper.JoinableTaskFactory.Run(() => superseded.WaitAsync(TimeSpan.FromSeconds(10)));

        _loader.IsLazy.Should().BeFalse();
    }

    [Test]
    public void Superseded_load_should_not_hide_that_the_displayed_nodes_are_lazy()
    {
        _loader.OnRefresh();
        Nodes displayed = _loader.TryLoad(_repository.Module)!;
        Display(displayed);

        _loader.TryLoad(_repository.Module).Should().NotBeNull("this is the late result of a superseded load, which is not displayed");

        _loader.IsLazy.Should().BeTrue();
    }

    private void Display(Nodes nodes)
    {
        _tree.Nodes.Clear();
        _tree.Nodes.AddNodes(nodes);
    }
}
