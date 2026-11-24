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
    private readonly List<Exception> _exceptions = [];
    private readonly ThreadExceptionEventHandler? _previousHandler;

    public UITestThreadExceptions()
    {
        _previousHandler = GetCurrentHandler();
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

    private static ThreadExceptionEventHandler? GetCurrentHandler()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        object? context = typeof(Application).GetNestedType("ThreadContext", flags)
            ?.GetMethod("FromCurrent", flags, [])
            ?.Invoke(obj: null, parameters: null);
        for (Type? type = context?.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField("_threadExceptionHandler", flags) is FieldInfo field)
            {
                return (ThreadExceptionEventHandler?)field.GetValue(context);
            }
        }

        return null;
    }

    private static void LogThreadException(object sender, ThreadExceptionEventArgs e)
        => Console.WriteLine($"Unhandled exception of the UI thread after the test:\n{e.Exception}");

    private void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Console.WriteLine($"Unhandled exception of the UI thread:\n{e.Exception}\nhandled at:\n{Environment.StackTrace}");
        _exceptions.Add(e.Exception);
    }
}
