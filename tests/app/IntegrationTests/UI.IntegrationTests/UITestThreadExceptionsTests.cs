using NUnit.Framework;

namespace GitExtensions.UITests;

/// <remarks>
///  No window is shown: there is always a handler while an exception is reported.
/// </remarks>
[TestFixture]
public sealed class UITestThreadExceptionsTests
{
    [Test]
    public void Exceptions_should_be_collected_and_the_previous_handler_restored()
    {
        List<string> previousHandlerMessages = [];
        bool isCollected = false;
        bool isPreviousRestored = false;

        RunOnNewThread(() =>
        {
            ThreadExceptionEventHandler previous = (_, e) => previousHandlerMessages.Add(e.Exception.Message);
            Application.ThreadException += previous;

            UITestThreadExceptions exceptions = new();
            Application.OnThreadException(new InvalidOperationException("collected"));
            exceptions.Dispose();
            isPreviousRestored = UITestThreadExceptions.TryGetHandler(UITestThreadExceptions.GetCurrentThreadContext(), out ThreadExceptionEventHandler? handler)
                && handler == previous;

            Action throwIfAny = exceptions.ThrowIfAny;
            isCollected = throwIfAny.Should().Throw<InvalidOperationException>().Which.Message == "collected";
        });

        isCollected.Should().BeTrue();
        isPreviousRestored.Should().BeTrue();
        previousHandlerMessages.Should().BeEmpty();
    }

    [Test]
    public void Handler_should_remain_if_there_was_none()
    {
        bool isKnown = false;
        ThreadExceptionEventHandler? handlerBefore = null;
        ThreadExceptionEventHandler? handlerAfter = null;

        RunOnNewThread(() =>
        {
            isKnown = UITestThreadExceptions.TryGetHandler(UITestThreadExceptions.GetCurrentThreadContext(), out handlerBefore);
            new UITestThreadExceptions().Dispose();
            UITestThreadExceptions.TryGetHandler(UITestThreadExceptions.GetCurrentThreadContext(), out handlerAfter);
        });

        isKnown.Should().BeTrue();
        handlerBefore.Should().BeNull();
        handlerAfter.Should().NotBeNull();
    }

    [Test]
    public void TryGetHandler_should_tell_that_the_handler_is_unknown()
    {
        UITestThreadExceptions.TryGetHandler(threadContext: null, out _).Should().BeFalse();
        UITestThreadExceptions.TryGetHandler(new object(), out _).Should().BeFalse();
        UITestThreadExceptions.TryGetHandler(new ContextWithOtherFieldType(), out _).Should().BeFalse();
        UITestThreadExceptions.TryGetHandler(new ContextWithoutHandler(), out ThreadExceptionEventHandler? handler).Should().BeTrue();
        handler.Should().BeNull();
    }

    private static void RunOnNewThread(Action action)
    {
        Exception? exception = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
        exception.Should().BeNull();
    }

#pragma warning disable CS0169, CS0414 // Read by reflection
    private sealed class ContextWithOtherFieldType
    {
        private readonly string? _threadExceptionHandler = "other";
    }

    private sealed class ContextWithoutHandler
    {
        private readonly ThreadExceptionEventHandler? _threadExceptionHandler = null;
    }
#pragma warning restore CS0169, CS0414
}
