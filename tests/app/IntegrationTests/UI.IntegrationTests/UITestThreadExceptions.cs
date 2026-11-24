using System.Reflection;
using System.Runtime.ExceptionServices;

namespace GitExtensions.UITests;

/// <summary>
///  Collects the exceptions which WinForms would show in a modal dialog of the UI thread of the test.
/// </summary>
/// <remarks>
///  <see cref="Application.ThreadException"/> has exactly one handler per thread, adding one replaces the previous one.
///  So this must be created on the thread of the test, and the previous handler is restored when disposed.
///  If there is none, a handler which only logs remains, in order to not show a dialog after the test has ended.
/// </remarks>
internal sealed class UITestThreadExceptions : IDisposable
{
    private const BindingFlags _bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly List<Exception> _exceptions = [];
    private readonly ThreadExceptionEventHandler? _previousHandler;

    /// <exception cref="InvalidOperationException">
    ///  The current handler cannot be determined: it would be lost, so the test must not be run.
    /// </exception>
    public UITestThreadExceptions()
    {
        if (!TryGetHandler(GetCurrentThreadContext(), out _previousHandler))
        {
            throw new InvalidOperationException($"The {nameof(Application.ThreadException)} handler of the thread cannot be determined, WinForms has changed.");
        }

        Application.ThreadException += OnThreadException;
    }

    public void Dispose()
    {
        Application.ThreadException += _previousHandler ?? LogThreadException;
    }

    /// <summary>
    ///  Fails the test with the first exception.
    /// </summary>
    public void ThrowIfAny()
    {
        if (_exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Throw(_exceptions[0]);
        }

        if (_exceptions.Count > 1)
        {
            throw new AggregateException(_exceptions);
        }
    }

    internal static object? GetCurrentThreadContext()
        => typeof(Application).GetNestedType("ThreadContext", _bindingFlags)
            ?.GetMethod("FromCurrent", _bindingFlags, [])
            ?.Invoke(obj: null, parameters: null);

    /// <summary>
    ///  Gets the handler of the thread of <paramref name="threadContext"/>, <see langword="null"/> if none is registered.
    /// </summary>
    /// <returns><see langword="false"/> if it cannot be determined whether a handler is registered.</returns>
    internal static bool TryGetHandler(object? threadContext, out ThreadExceptionEventHandler? handler)
    {
        handler = null;
        for (Type? type = threadContext?.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField("_threadExceptionHandler", _bindingFlags) is FieldInfo field
                && typeof(ThreadExceptionEventHandler).IsAssignableFrom(field.FieldType))
            {
                handler = (ThreadExceptionEventHandler?)field.GetValue(threadContext);
                return true;
            }
        }

        return false;
    }

    private static void LogThreadException(object sender, ThreadExceptionEventArgs e)
        => Console.WriteLine($"Unhandled exception of the UI thread after the test:\n{e.Exception}");

    private void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Console.WriteLine($"Unhandled exception of the UI thread:\n{e.Exception}\nhandled at:\n{Environment.StackTrace}");
        _exceptions.Add(e.Exception);
    }
}
