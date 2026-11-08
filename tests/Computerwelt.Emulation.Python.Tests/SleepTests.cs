using System.Diagnostics;
using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// <c>time.sleep</c> waits for real, up to <see cref="ExecutionLimits.MaxSleep"/>, shortened to a
/// host's budget and ended by cancellation.
/// </summary>
public sealed class SleepTests
{
    [Fact]
    public void The_default_maximum_is_one_hour() =>
        Assert.Equal(TimeSpan.FromHours(1), ExecutionLimits.Default.MaxSleep);

    [Fact]
    public void A_sleep_waits_for_the_time_it_asked_for()
    {
        var watch = Stopwatch.StartNew();
        var result = new PythonRunner().Run("import time\ntime.sleep(0.3)\nprint('done')");

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("done\n", result.Stdout);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(280), $"slept {watch.Elapsed}");
    }

    [Fact]
    public void The_program_sees_the_time_it_slept_pass()
    {
        var result = new PythonRunner().Run("""
            import time
            start = time.monotonic()
            time.sleep(0.2)
            print(time.monotonic() - start >= 0.2)
            """);

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("True\n", result.Stdout);
    }

    [Fact]
    public void A_sleep_longer_than_the_maximum_raises_naming_the_maximum_without_waiting()
    {
        var watch = Stopwatch.StartNew();
        var result = new PythonRunner().Run("""
            import time
            try:
                time.sleep(7200)
            except ValueError as e:
                print(e)
            """);

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("sleep length 7200 seconds exceeds the maximum of 3600 seconds this sandbox allows\n", result.Stdout);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
    }

    [Fact]
    public void The_maximum_is_the_hosts_to_set()
    {
        var result = new PythonRunner(ExecutionLimits.Default with { MaxSleep = TimeSpan.FromSeconds(1) })
            .Run("import time\ntime.sleep(1.5)");

        Assert.False(result.Succeeded);
        Assert.Contains("ValueError: sleep length 1.5 seconds exceeds the maximum of 1 seconds", result.Traceback, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sleep_is_shortened_to_the_hosts_budget()
    {
        var watch = Stopwatch.StartNew();
        var result = new PythonRunner { SleepBudget = () => TimeSpan.FromMilliseconds(100) }
            .Run("import time\ntime.sleep(30)\nprint('woke')");

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("woke\n", result.Stdout);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
    }

    [Fact]
    public void Cancelling_the_run_ends_a_sleep()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();

        // A host exception, as an interrupt between instructions is: no `except` in the program can catch it.
        Assert.ThrowsAny<OperationCanceledException>(() => new PythonRunner().Run("""
            import time
            try:
                time.sleep(30)
            except BaseException:
                print('caught')
            """, cancellationToken: cancel.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
    }
}
