using System.Diagnostics;
using Xunit;

namespace Computerwelt.Emulation.Bash.Tests;

/// <summary>
/// <c>sleep</c> waits for real, up to <see cref="ExecutionLimits.MaxSleep"/>, and refuses longer.
/// </summary>
public sealed class SleepTests
{
    private static Bash Shell(TimeSpan? maxSleep = null) =>
        Bash.CreateBuilder()
            .WithLimits(ExecutionLimits.Default with { MaxSleep = maxSleep ?? ExecutionLimits.Default.MaxSleep })
            .Build();

    [Fact]
    public void The_default_maximum_is_one_hour() =>
        Assert.Equal(TimeSpan.FromHours(1), ExecutionLimits.Default.MaxSleep);

    [Fact]
    public async Task A_sleep_waits_for_the_time_it_asked_for()
    {
        var watch = Stopwatch.StartNew();
        var result = await Shell().ExecAsync("sleep 0.3; echo done");

        Assert.Equal("done\n", result.Stdout.ToString());
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(280), $"slept {watch.Elapsed}");
    }

    [Fact]
    public async Task A_sleep_longer_than_the_maximum_fails_naming_the_maximum_without_waiting()
    {
        var watch = Stopwatch.StartNew();
        var result = await Shell().ExecAsync("sleep 2h; echo exit: $?");

        Assert.Equal("exit: 1\n", result.Stdout.ToString());
        Assert.Equal("sleep: 7200 seconds exceeds the maximum sleep of 3600 seconds this sandbox allows\n", result.Stderr.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
    }

    [Fact]
    public async Task The_maximum_is_the_hosts_to_set()
    {
        var result = await Shell(TimeSpan.FromSeconds(1)).ExecAsync("sleep 1.5; echo exit: $?");

        Assert.Equal("exit: 1\n", result.Stdout.ToString());
        Assert.Contains("maximum sleep of 1 seconds", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_maximum_applies_to_the_sum_of_the_operands()
    {
        var result = await Shell(TimeSpan.FromSeconds(1)).ExecAsync("sleep 0.6 0.6; echo exit: $?");

        Assert.Equal("exit: 1\n", result.Stdout.ToString());
        Assert.Contains("1.2 seconds exceeds", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duration_too_long_to_represent_is_refused_by_the_maximum_rather_than_crashing()
    {
        var result = await Shell().ExecAsync("sleep 1e300; echo exit: $?");

        Assert.Equal("exit: 1\n", result.Stdout.ToString());
        Assert.Contains("exceeds the maximum sleep of 3600 seconds", result.Stderr.ToString(), StringComparison.Ordinal);
    }
}
