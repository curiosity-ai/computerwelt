using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Python;
using Xunit;

namespace Computerwelt.Tests;

/// <summary>
/// A notebook session and a shell over one virtual filesystem.
/// </summary>
public sealed class NotebookTests
{
    [Fact]
    public async Task A_notebook_and_the_shell_see_the_same_files()
    {
        var bash = Bash.CreateBuilder().WithWorkingDirectory("/").WithPython().Build();
        await bash.ExecAsync("mkdir -p /data && printf 'a,b\\n1,2\\n' > /data/in.csv");

        using var notebook = new PythonOptions().StartSession(bash.FileSystem, "/data");

        var read = notebook.Run("""
            import csv
            rows = list(csv.reader(open('in.csv')))
            len(rows)
            """);
        Assert.Equal("2", read.Outputs[0].Text);

        Assert.True(notebook.Run("open('out.txt', 'w').write(str(rows[1]))").Succeeded);

        var shell = await bash.ExecAsync("cat /data/out.txt");
        Assert.Equal("['1', '2']", shell.Stdout.ToString());
    }

    [Fact]
    public void A_notebook_without_a_filesystem_still_runs_but_cannot_open_files()
    {
        using var notebook = new PythonRunner().StartSession();

        Assert.True(notebook.Run("x = [i * i for i in range(4)]").Succeeded);
        Assert.Equal("[0, 1, 4, 9]", notebook.Run("x").Outputs[0].Text);
        Assert.Equal("NameError", notebook.Run("open('/etc/passwd')").ErrorName);
    }

    [Fact]
    public void Variables_lists_what_the_cells_defined_and_hides_the_session_names()
    {
        using var notebook = new PythonRunner().StartSession();
        notebook.Run("import math\nxs = [1, 2, 3]\nname = 'ada'\n_hidden = 1\n2 + 2");

        var variables = notebook.Variables();

        Assert.Equal(["name", "xs"], variables.Select(v => v.Name));
        Assert.Equal("list", variables.Single(v => v.Name == "xs").Type);
        Assert.Equal("[1, 2, 3]", variables.Single(v => v.Name == "xs").Summary);
    }

    [Fact]
    public void Complete_offers_names_in_scope_and_attributes_without_calling_anything()
    {
        using var notebook = new PythonRunner().StartSession();
        notebook.Run("import math\ncalls = []\ndef f():\n    calls.append(1)\n    return 'x'\nvalue = 'hello'");

        var scope = notebook.Complete("val", 3);
        Assert.Contains("value", scope.Items);
        Assert.Equal(0, scope.Start);

        var module = notebook.Complete("math.sq", 7);
        Assert.Equal(["sqrt"], module.Items);
        Assert.Equal(5, module.Start);

        var text = notebook.Complete("value.up", 8);
        Assert.Contains("upper", text.Items);

        // `f().` needs a call to resolve, which completion never makes.
        Assert.Empty(notebook.Complete("f().up", 6).Items);
        Assert.Equal("[]", notebook.Run("calls").Outputs[0].Text);

        Assert.Empty(notebook.Complete("nothing_like_this.x", 19).Items);
    }
}
