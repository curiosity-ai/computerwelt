using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// <see cref="PythonSession"/>: cells that share one namespace, as a notebook's do.
/// </summary>
public sealed class SessionTests
{
    [Fact]
    public void State_defined_in_one_cell_is_there_in_the_next()
    {
        using var session = new PythonRunner().StartSession();

        Assert.True(session.Run("import math\nx = 21\ndef double(v):\n    return v * 2").Succeeded);

        var result = session.Run("double(x) + math.floor(0.5)");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ExecutionCount);
        Assert.Equal("42", Assert.Single(result.Outputs).Text);
        Assert.Equal(CellOutputKind.Result, result.Outputs[0].Kind);
    }

    [Fact]
    public void A_class_defined_earlier_still_works_with_super_and_inheritance()
    {
        using var session = new PythonRunner().StartSession();

        session.Run("class A:\n    def hi(self): return 'A'\nclass B(A):\n    def hi(self): return 'B' + super().hi()");

        Assert.Equal("'BA'", session.Run("B().hi()").Outputs[0].Text);
    }

    [Fact]
    public void Outputs_keep_the_order_they_happened_in()
    {
        using var session = new PythonRunner().StartSession();

        var result = session.Run("""
            print('first')
            display(1 + 1)
            print('second')
            'last'
            """);

        Assert.Collection(
            result.Outputs,
            o => Assert.Equal(("first\n", "stdout"), (o.Text, o.StreamName)),
            o => Assert.Equal((CellOutputKind.Display, "2"), (o.Kind, o.Text)),
            o => Assert.Equal("second\n", o.Text),
            o => Assert.Equal((CellOutputKind.Result, "'last'"), (o.Kind, o.Text)));
    }

    [Fact]
    public void A_trailing_semicolon_or_a_None_value_shows_nothing()
    {
        using var session = new PythonRunner().StartSession();

        Assert.Empty(session.Run("1 + 1;").Outputs);
        Assert.Empty(session.Run("None").Outputs);
        Assert.Empty(session.Run("x = 5").Outputs);
    }

    [Fact]
    public void Rich_representations_become_a_mime_bundle()
    {
        using var session = new PythonRunner().StartSession();

        var result = session.Run("""
            class Table:
                def __repr__(self): return 'Table()'
                def _repr_html_(self): return '<table></table>'
                def _repr_markdown_(self): return '| a |'
            Table()
            """);

        var bundle = Assert.Single(result.Outputs).Data;
        Assert.Equal("Table()", bundle["text/plain"]);
        Assert.Equal("<table></table>", bundle["text/html"]);
        Assert.Equal("| a |", bundle["text/markdown"]);
    }

    [Fact]
    public void IPython_display_helpers_tag_their_data()
    {
        using var session = new PythonRunner().StartSession();

        var result = session.Run("""
            from IPython.display import display, HTML, Markdown, Image
            display(HTML('<b>hi</b>'), Markdown('# Title'))
            display(Image(data=b'\x89PNG', format='png'))
            """);

        Assert.Equal(3, result.Outputs.Count);
        Assert.Equal("<b>hi</b>", result.Outputs[0].Data["text/html"]);
        Assert.Equal("# Title", result.Outputs[1].Data["text/markdown"]);
        Assert.Equal(Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]), result.Outputs[2].Data["image/png"]);
    }

    [Fact]
    public void Clear_output_drops_what_the_cell_showed_so_far()
    {
        using var session = new PythonRunner().StartSession();

        var result = session.Run("""
            from IPython.display import clear_output
            print('progress 1')
            clear_output()
            print('done')
            """);

        Assert.Equal("done\n", Assert.Single(result.Outputs).Text);
    }

    [Fact]
    public void An_error_ends_the_cell_but_not_the_session()
    {
        using var session = new PythonRunner().StartSession();

        session.Run("total = 10");
        var failed = session.Run("print('before')\n1 / 0");

        Assert.False(failed.Succeeded);
        Assert.Equal("ZeroDivisionError", failed.ErrorName);
        Assert.Equal(CellOutputKind.Error, failed.Outputs[^1].Kind);
        Assert.Contains("ZeroDivisionError: division by zero", failed.Traceback, StringComparison.Ordinal);
        Assert.Equal("before\n", failed.Outputs[0].Text);

        Assert.Equal("10", session.Run("total").Outputs[0].Text);
    }

    [Fact]
    public void A_syntax_error_is_reported_as_one()
    {
        using var session = new PythonRunner().StartSession();

        var result = session.Run("def broken(:");

        Assert.False(result.Succeeded);
        Assert.Equal("SyntaxError", result.ErrorName);
    }

    [Fact]
    public void Every_cell_gets_the_full_instruction_budget()
    {
        using var session = new PythonRunner(new ExecutionLimits { MaxInstructions = 200_000 }).StartSession();

        // Each cell spends most of the budget; were it shared, the third would run out.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(session.Run("s = 0\nfor i in range(20000): s += i").Succeeded);
        }

        var runaway = session.Run("while True: pass");
        Assert.False(runaway.Succeeded);
        Assert.Contains("instruction limit exceeded", runaway.Traceback, StringComparison.Ordinal);

        Assert.True(session.Run("s").Succeeded);
    }

    [Fact]
    public void Cancellation_interrupts_a_cell_and_cannot_be_caught_by_it()
    {
        using var session = new PythonRunner(new ExecutionLimits { MaxInstructions = long.MaxValue }).StartSession();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = session.Run("""
            while True:
                try:
                    while True:
                        pass
                except BaseException:
                    pass
            """, cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal("KeyboardInterrupt", result.ErrorName);
        Assert.True(session.Run("1").Succeeded);
    }

    [Fact]
    public void Results_are_kept_as_underscore_names_and_in_Out()
    {
        using var session = new PythonRunner().StartSession();

        session.Run("6 * 7");
        var result = session.Run("(_, _1, Out[1])");

        Assert.Equal("(42, 42, 42)", result.Outputs[0].Text);
    }

    [Fact]
    public void Sys_exit_ends_the_cell_only()
    {
        using var session = new PythonRunner().StartSession();

        var result = session.Run("import sys\nsys.exit(3)");

        Assert.False(result.Succeeded);
        Assert.Equal("SystemExit", result.ErrorName);
        Assert.True(session.Run("2").Succeeded);
    }

    [Fact]
    public void Reset_clears_the_namespace()
    {
        using var session = new PythonRunner().StartSession();

        session.Run("x = 1");
        session.Reset();

        var result = session.Run("x");
        Assert.Equal("NameError", result.ErrorName);
        Assert.Equal(1, result.ExecutionCount);
    }

    [Fact]
    public void Two_sessions_share_nothing()
    {
        var runner = new PythonRunner();
        using var first = runner.StartSession();
        using var second = runner.StartSession();

        first.Run("import json\njson.shared = 1\nvalue = 'first'");

        Assert.Equal("NameError", second.Run("value").ErrorName);
        Assert.Equal("False", second.Run("import json\nhasattr(json, 'shared')").Outputs[0].Text);
    }

    [Fact]
    public void Libraries_are_told_when_the_session_ends()
    {
        var log = new List<string>();
        var runner = new PythonRunner();

        runner.Libraries.Add(PythonLibrary.FromFactory("leases", context =>
        {
            context.Machine.WhenRunCompleted(() => log.Add("released"));
            return context.Module();
        }));

        using (var session = runner.StartSession())
        {
            session.Run("import leases");
            session.Run("import leases");
            Assert.Empty(log);
        }

        Assert.Equal(["released"], log);
    }

    [Fact]
    public async Task Pandas_follows_its_session_across_threads_and_never_into_another()
    {
        using var first = new PythonRunner().StartSession();
        using var second = new PythonRunner().StartSession();

        // Each cell on a fresh thread-pool thread, the two tenants interleaved: a callback a
        // frame runs must reach its own session's machine whichever thread the cell is on.
        await Task.Run(() => first.Run("import pandas as pd\nscale = 10\ndf = pd.DataFrame({'a': [1, 2]})"));
        await Task.Run(() => second.Run("import pandas as pd\nscale = 1000\ndf = pd.DataFrame({'a': [1, 2]})"));

        var a = await Task.Run(() => first.Run("df['a'].apply(lambda v: v * scale).tolist()"));
        var b = await Task.Run(() => second.Run("df['a'].apply(lambda v: v * scale).tolist()"));

        Assert.Equal("[10, 20]", a.Outputs[0].Text);
        Assert.Equal("[1000, 2000]", b.Outputs[0].Text);

        var html = await Task.Run(() => first.Run("df"));
        Assert.Contains("<table", html.Outputs[0].Data["text/html"], StringComparison.Ordinal);
    }
}
