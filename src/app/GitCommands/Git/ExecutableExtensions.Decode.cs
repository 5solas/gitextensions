using System.Text;

namespace GitCommands;

public static partial class ExecutableExtensions
{
    /// <summary>
    ///  Decodes the bytes of an output like <see cref="ExecuteAsync"/> does.
    /// </summary>
    internal static string DecodeOutput(byte[] output, Encoding outputEncoding, bool stripAnsiEscapeCodes)
        => CleanString(stripAnsiEscapeCodes, outputEncoding.GetString(output));
}
