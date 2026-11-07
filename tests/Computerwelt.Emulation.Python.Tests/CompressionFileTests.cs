using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// A compressed file the program never closed is still written when the run ends.
/// </summary>
/// <remarks>
/// <c>gzip.open</c> and <c>ZipFile</c> compress what was written when they are closed, and a
/// script that forgets <c>close()</c> — or dies on an exception first — would otherwise leave
/// an empty file behind where CPython's finalizers would have written one. The fixtures cannot
/// see this, because the file only appears after the program has finished.
/// </remarks>
public sealed class CompressionFileTests
{
    [Fact]
    public void An_unclosed_gzip_writer_is_flushed_at_the_end_of_the_run()
    {
        var files = new BinaryFileTests.ByteFileSystem();

        var result = new PythonRunner { FileSystem = files }.Run(
            """
            import gzip
            handle = gzip.open('/notes.gz', 'wt')
            handle.write('kept\n')
            """);

        Assert.True(result.Succeeded, result.Traceback);

        var check = new PythonRunner { FileSystem = files }.Run(
            """
            import gzip
            assert gzip.open('/notes.gz', 'rt').read() == 'kept\n'
            """);

        Assert.True(check.Succeeded, check.Traceback);
    }

    [Fact]
    public void An_unclosed_zip_archive_is_written_even_when_the_program_fails()
    {
        var files = new BinaryFileTests.ByteFileSystem();

        var result = new PythonRunner { FileSystem = files }.Run(
            """
            import zipfile
            archive = zipfile.ZipFile('/bundle.zip', 'w')
            archive.writestr('a.txt', 'alpha')
            raise RuntimeError('the script dies before closing')
            """);

        Assert.False(result.Succeeded);

        var check = new PythonRunner { FileSystem = files }.Run(
            """
            import zipfile
            with zipfile.ZipFile('/bundle.zip') as archive:
                assert archive.namelist() == ['a.txt']
                assert archive.read('a.txt') == b'alpha'
            """);

        Assert.True(check.Succeeded, check.Traceback);
    }

    [Fact]
    public void A_zip_member_that_lies_about_its_content_is_refused()
    {
        var files = new BinaryFileTests.ByteFileSystem();

        var result = new PythonRunner { FileSystem = files }.Run(
            """
            import zipfile
            with zipfile.ZipFile('/good.zip', 'w') as archive:
                archive.writestr('a.txt', 'alpha' * 10, compress_type=zipfile.ZIP_STORED)
            data = open('/good.zip', 'rb').read()
            # Flip a byte of the stored content, leaving the recorded CRC as it was.
            at = data.index(b'alphaalpha')
            corrupt = data[:at] + b'A' + data[at + 1:]
            with open('/bad.zip', 'wb') as handle:
                handle.write(corrupt)
            with zipfile.ZipFile('/bad.zip') as archive:
                assert archive.testzip() == 'a.txt'
                try:
                    archive.read('a.txt')
                    raise AssertionError('read a corrupt member')
                except zipfile.BadZipFile as error:
                    assert 'Bad CRC-32' in str(error), str(error)
            """);

        Assert.True(result.Succeeded, result.Traceback);
    }
}
