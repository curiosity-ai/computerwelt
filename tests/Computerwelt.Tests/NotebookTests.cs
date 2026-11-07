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
}
