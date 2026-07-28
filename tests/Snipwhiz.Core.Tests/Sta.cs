namespace Snipwhiz.Core.Tests;

/// <summary>
/// Runs work on an STA thread and rethrows any failure on the caller's thread.
///
/// xUnit runs tests on the thread pool, which is MTA. The clipboard,
/// <c>RenderTargetBitmap</c> and anything else touching the WPF render stack are
/// apartment-threaded and throw there.
///
/// <para>This is a helper rather than the <c>[StaFact]</c> attribute the plan
/// named. A real attribute needs either the <c>Xunit.StaFact</c> package — which
/// the plan's own Global Constraints forbid — or a custom xUnit test framework,
/// which is a few hundred lines to save one call per test. <c>ClipboardFormatTests</c>
/// already did this inline; this is that code, generalised.</para>
/// </summary>
internal static class Sta
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public static void Run(Action action) => Run<object?>(() => { action(); return null; });

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { failure = e; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Join's return value is checked, unlike the version this replaces: a
        // deadlocked STA thread there returned the default result and the test
        // passed. A check that hangs is not evidence, and one that quietly
        // succeeds after hanging is worse.
        if (!thread.Join(Limit))
            throw new Xunit.Sdk.XunitException($"STA work did not finish within {Limit.TotalSeconds:N0}s.");

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
        return result;
    }
}
