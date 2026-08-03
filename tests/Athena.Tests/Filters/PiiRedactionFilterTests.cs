using Athena.Filters;

namespace Athena.Tests.Filters;

public sealed class PiiRedactionFilterTests
{
    [Theory]
    [InlineData("Contact me at jane.doe@example.com about d516.",
                "Contact me at [email redacted] about d516.")]
    [InlineData("My card is 4111 1111 1111 1111 thanks.",
                "My card is [number redacted] thanks.")]
    [InlineData("SSN 123-45-6789 should never leave the process.",
                "SSN [id redacted] should never leave the process.")]
    [InlineData("Call +44 20 7946 0958 tomorrow.",
                "Call [number redacted] tomorrow.")]
    public void Redact_MasksPii(string input, string expected)
        => Assert.Equal(expected, PiiRedactionFilter.Redact(input));

    [Fact]
    public void Redact_LeavesRegulatoryIdentifiersAlone()
    {
        // The corpus lives on identifiers ("d516", "SP 800-207", "p.11") — masking must not
        // eat the very tokens lexical retrieval depends on.
        const string text = "Principle 6 of d516, NIST SP 800-207, and [Title, p.11] stay intact.";

        Assert.Equal(text, PiiRedactionFilter.Redact(text));
    }
}
