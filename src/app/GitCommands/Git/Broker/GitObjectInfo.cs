using GitExtensions.Extensibility.Git;

namespace GitCommands.Git.Broker;

/// <summary>
///  The header of an object as reported by <c>git cat-file</c>.
/// </summary>
internal readonly record struct GitObjectInfo(ObjectId ObjectId, string Type, long Size)
{
    public const string Blob = "blob";
    public const string Submodule = "submodule";
}
