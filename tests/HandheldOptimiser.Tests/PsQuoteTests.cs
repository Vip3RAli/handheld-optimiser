using HandheldOptimiser.Services;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Names pasted into elevated scripts as single-quoted literals. PowerShell ends such a literal at any of
/// five quote characters, so every one of them has to be doubled, not only the ASCII apostrophe.
/// </summary>
public sealed class PsQuoteTests
{
    [Theory]
    [InlineData("'")]
    [InlineData("\u2018")]
    [InlineData("\u2019")]
    [InlineData("\u201A")]
    [InlineData("\u201B")]
    public void Every_single_quote_form_is_doubled(string quote)
    {
        Assert.Equal($"a{quote}{quote}b", PsQuote.Escape($"a{quote}b"));
    }

    [Fact]
    public void Mixed_quotes_are_each_doubled_as_themselves()
    {
        Assert.Equal("''\u2018\u2018x\u2019\u2019\u201A\u201A\u201B\u201B", PsQuote.Escape("'\u2018x\u2019\u201A\u201B"));
    }

    [Fact]
    public void Text_without_quotes_is_unchanged()
    {
        Assert.Equal(@"C:\Program Files\Handheld Optimiser", PsQuote.Escape(@"C:\Program Files\Handheld Optimiser"));
    }

    [Fact]
    public void Double_quotes_are_left_alone()
    {
        // Inside a single-quoted literal a double quote is just a character.
        Assert.Equal("say \"hi\" \u201Chi\u201D", PsQuote.Escape("say \"hi\" \u201Chi\u201D"));
    }

    [Fact]
    public void Literal_wraps_the_escaped_text_in_quotes()
    {
        Assert.Equal("'HID Keyboard\u2019\u2019; Remove-Item x'", PsQuote.Literal("HID Keyboard\u2019; Remove-Item x"));
    }

    [Theory]
    [InlineData("Keyboard\nRemove-Item x", true)]
    [InlineData("Keyboard\r", true)]
    [InlineData("Tab\tName", true)]
    [InlineData("HID-compliant mouse (002)", false)]
    public void Control_characters_are_detected(string name, bool expected)
    {
        Assert.Equal(expected, PsQuote.HasControlCharacters(name));
    }
}
