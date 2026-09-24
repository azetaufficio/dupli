using Dupli.Agent.Cli;

namespace Dupli.Agent.Tests.Cli;

public sealed class CommandsTests
{
    [Fact]
    public void ReadSecretValue_reads_piped_stdin_to_eof_and_trims_trailing_newline()
    {
        using var reader = new StringReader("s3kr3t-value\n");

        var value = Commands.ReadSecretValue(interactive: false, reader);

        Assert.Equal("s3kr3t-value", value);
    }

    [Fact]
    public void ReadSecretValue_trims_crlf_from_piped_stdin()
    {
        using var reader = new StringReader("s3kr3t-value\r\n");

        var value = Commands.ReadSecretValue(interactive: false, reader);

        Assert.Equal("s3kr3t-value", value);
    }
}
