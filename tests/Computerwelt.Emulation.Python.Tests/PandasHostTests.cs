using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// The parts of <c>pandas</c> that are about the host rather than the language: a run with no
/// filesystem, and two runs on one thread sharing nothing.
/// </summary>
/// <remarks>
/// The fixtures in <c>tests/monty-extensions/pandas__*.py</c> pin the behaviour; they all run
/// with the fixture filesystem attached, so the absence of one is tested here.
/// </remarks>
public sealed class PandasHostTests
{
    [Fact]
    public void Without_a_filesystem_pandas_works_in_memory_and_file_reads_raise_OSError()
    {
        var result = new PythonRunner().Run("""
            import io
            import pandas as pd
            df = pd.read_csv(io.StringIO('a,b\n1,2\n'))
            assert df['b'].tolist() == [2]
            assert df.to_csv(index=False) == 'a,b\n1,2\n'
            try:
                pd.read_csv('/data.csv')
                raise AssertionError('read a file with no filesystem')
            except OSError as e:
                print(type(e).__name__)
            try:
                df.to_csv('/out.csv')
                raise AssertionError('wrote a file with no filesystem')
            except OSError:
                print('refused')
            """);

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("OSError\nrefused\n", result.Stdout);
    }

    [Fact]
    public void Options_and_callbacks_belong_to_their_own_run()
    {
        // The first run changes a display option and leaves a frame behind in its globals;
        // the second, on the same thread, must see the default and its own machine.
        var first = new PythonRunner().Run("""
            import pandas as pd
            pd.set_option('display.max_rows', 4)
            frame = pd.DataFrame({'x': range(10)})
            print(repr(frame).splitlines()[-1])
            """);

        var second = new PythonRunner().Run("""
            import pandas as pd
            print(pd.get_option('display.max_rows'))
            print(len(repr(pd.DataFrame({'x': range(10)})).splitlines()))
            print(pd.Series([1, 2]).apply(lambda v: v * 3).tolist())
            """);

        Assert.True(first.Succeeded, first.Traceback);
        Assert.Equal("[10 rows x 1 columns]\n", first.Stdout);
        Assert.True(second.Succeeded, second.Traceback);
        Assert.Equal("60\n11\n[3, 6]\n", second.Stdout);
    }

    [Fact]
    public void A_host_exception_never_escapes_a_pandas_call()
    {
        // Each of these reaches deep into the module; every failure must arrive as a
        // Python exception the program can catch, never as a crash of the host.
        var result = new PythonRunner().Run("""
            import pandas as pd
            caught = 0
            for case in [
                lambda: pd.Series([1, 2]).iloc[9],
                lambda: pd.DataFrame({'a': [1]}).loc[0, 'missing'],
                lambda: pd.Series([2 ** 70]).astype('int64'),
                lambda: pd.DataFrame([[1, 2], [3]]).iloc[:, 5],
                lambda: pd.to_datetime('not a date'),
                lambda: pd.Series(['a']).astype(float),
                lambda: pd.DataFrame({'a': [1]}).query('a >'),
            ]:
                try:
                    case()
                except Exception:
                    caught += 1
            print(caught)
            """);

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal("7\n", result.Stdout);
    }
}
