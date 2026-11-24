using System.Reflection;
using System.Text;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitCommands.Git.Broker;

/// <summary>
///  A module whose git executable provides an output which has been read already, whatever is run.
/// </summary>
/// <remarks>
///  So the output is parsed in the same way as the one of a git process. To be used for this only.
/// </remarks>
internal class KnownAttributesModule : DispatchProxy
{
    private IGitModule _module = null!;
    private KnownOutputExecutable _executable = null!;

    public static IGitModule Create(IGitModule module, byte[] output)
    {
        IGitModule proxy = Create<IGitModule, KnownAttributesModule>();
        KnownAttributesModule instance = (KnownAttributesModule)(object)proxy;
        instance._module = module;
        instance._executable = new KnownOutputExecutable(module.GitExecutable, output);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name == $"get_{nameof(IGitModule.GitExecutable)}"
            ? _executable
            : targetMethod.Invoke(_module, args);

    private sealed class KnownOutputExecutable(IExecutable executable, byte[] output) : IExecutable
    {
        public string WorkingDir => executable.WorkingDir;

        public string Command => executable.Command;

        public string PrefixArguments => executable.PrefixArguments;

        public IProcess Start(
            ArgumentString arguments = default,
            bool createWindow = false,
            bool redirectInput = false,
            bool redirectOutput = false,
            Encoding? outputEncoding = null,
            bool useShellExecute = false,
            bool throwOnErrorExit = true,
            CancellationToken cancellationToken = default)
            => new CannedProcess(output);
    }
}
