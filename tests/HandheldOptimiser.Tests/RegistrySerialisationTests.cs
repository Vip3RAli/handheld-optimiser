using System.Globalization;
using HandheldOptimiser.Services;
using Microsoft.Win32;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// The text form registry snapshots are journalled in. It is read back on a later run, maybe under a
/// different culture, so it must not depend on the one in use when it was written.
/// </summary>
public sealed class RegistrySerialisationTests
{
    /// <summary>Swedish writes negative numbers with U+2212, which the invariant parser does not accept.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _culture;
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void Dwords_round_trip_under_a_culture_with_a_unicode_minus(int value)
    {
        using var _ = new CultureScope("sv-SE");

        var text = RegistryHelper.SerialiseValue(value, RegistryValueKind.DWord);

        Assert.DoesNotContain("\u2212", text);
        Assert.Equal(value, RegistryHelper.DeserialiseValue(text, RegistryValueKind.DWord));
    }

    [Fact]
    public void Qwords_round_trip_under_a_culture_with_a_unicode_minus()
    {
        using var _ = new CultureScope("sv-SE");

        var text = RegistryHelper.SerialiseValue(-1234567890123L, RegistryValueKind.QWord);

        Assert.Equal("-1234567890123", text);
        Assert.Equal(-1234567890123L, RegistryHelper.DeserialiseValue(text, RegistryValueKind.QWord));
    }

    [Fact]
    public void Numbers_journalled_with_a_unicode_minus_by_older_versions_still_read()
    {
        Assert.Equal(-5, RegistryHelper.DeserialiseValue("\u22125", RegistryValueKind.DWord));
        Assert.Equal(-7L, RegistryHelper.DeserialiseValue("\u22127", RegistryValueKind.QWord));
    }

    [Fact]
    public void Dwords_above_int_max_from_older_journals_still_read()
    {
        Assert.Equal(-1, RegistryHelper.DeserialiseValue("4294967295", RegistryValueKind.DWord));
    }

    [Fact]
    public void An_empty_multi_string_round_trips_as_no_strings()
    {
        var text = RegistryHelper.SerialiseValue(Array.Empty<string>(), RegistryValueKind.MultiString);

        Assert.Equal(string.Empty, text);
        Assert.Empty((string[])RegistryHelper.DeserialiseValue(text, RegistryValueKind.MultiString));
    }

    [Fact]
    public void A_multi_string_round_trips()
    {
        string[] value = ["one", "", "three"];

        var text = RegistryHelper.SerialiseValue(value, RegistryValueKind.MultiString);

        Assert.Equal(value, (string[])RegistryHelper.DeserialiseValue(text, RegistryValueKind.MultiString));
    }

    [Fact]
    public void Expandable_strings_are_kept_as_written()
    {
        var text = RegistryHelper.SerialiseValue(@"%SystemRoot%\system32", RegistryValueKind.ExpandString);

        Assert.Equal(@"%SystemRoot%\system32", RegistryHelper.DeserialiseValue(text, RegistryValueKind.ExpandString));
    }
}
