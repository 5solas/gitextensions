namespace GitCommands.Git.Broker;

/// <summary>
///  A persistent git process has exited or answered something unexpected. It must not be used any longer.
/// </summary>
internal sealed class GitBrokerException(string message, Exception? innerException = null) : Exception(message, innerException);
