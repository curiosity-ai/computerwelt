using System.Diagnostics;
using Computerwelt.Emulation.Bash;
using Xunit;

namespace Computerwelt.Tests;

/// <summary>
/// <c>time.sleep</c> under the shell: the shell's limit, its timeout and its cancellation.
/// </summary>
public sealed class SleepTests
{
    private static Bash Shell(ExecutionLimits? limits = null) =>
        Bash.CreateBuilder().WithWorkingDirectory("/").WithLimits(limits ?? ExecutionLimits.Default).WithPython().Build();

    [Fact]
    public async Task Python_sleeps_for_real_from_the_shell()
    {
        var watch = Stopwatch.StartNew();
        var result = await Shell().ExecAsync("python -c 'import time; time.sleep(0.3); print(1)'");

        Assert.Equal("1\n", result.Stdout.ToString());
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(280), $"slept {watch.Elapsed}");
    }

    [Fact]
    public async Task One_maximum_bounds_both_sleeps()
    {
        var bash = Shell(ExecutionLimits.Default with { MaxSleep = TimeSpan.FromSeconds(1) });

        var shell  = await bash.ExecAsync("sleep 2");
        var python = await bash.ExecAsync("python -c 'import time; time.sleep(2)'");

        Assert.Contains("maximum sleep of 1 seconds", shell.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("exceeds the maximum of 1 seconds", python.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_timeout_ends_a_python_sleep()
    {
        var watch = Stopwatch.StartNew();
        var result = await Shell().ExecAsync("timeout 0.2 python -c 'import time; time.sleep(30)'; echo $?");

        Assert.Equal("124\n", result.Stdout.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
    }

    [Fact]
    public async Task A_python_sleep_is_shortened_to_what_is_left_of_the_shells_timeout()
    {
        var bash  = Shell(ExecutionLimits.Default with { Timeout = TimeSpan.FromMilliseconds(500) });
        var watch = Stopwatch.StartNew();

        // The same outcome as the shell's own sleep: the program wakes when the run's time is up,
        // and the run ends on its timeout before the next command.
        var result = await bash.ExecAsync("python -c 'import time; time.sleep(30)'; echo after");

        Assert.Equal(string.Empty, result.Stdout.ToString());
        Assert.Contains("execution timed out after 0.5s", result.Stderr.ToString(), StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
    }

    [Fact]
    public async Task A_timeout_ends_a_python_program_that_never_sleeps()
    {
        var watch = Stopwatch.StartNew();
        var result = await Shell().ExecAsync("timeout 0.2 python -c 'while True: pass'; echo $?");

        Assert.Equal("124\n", result.Stdout.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
    }
}
