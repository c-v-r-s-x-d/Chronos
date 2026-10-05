using System.Globalization;

namespace Chronos.App.Tests;

/// <summary>Sets the interface language for a test and restores it. The language is process-wide, so tests that touch it belong to <see cref="CultureBound"/> and run alone.</summary>
internal sealed class UiCulture : IDisposable
{
    private readonly CultureInfo _thread = CultureInfo.CurrentUICulture;

    private readonly CultureInfo? _newThreads = CultureInfo.DefaultThreadCurrentUICulture;

    private readonly CultureInfo _formatting = CultureInfo.CurrentCulture;

    /// <param name="formatting">The culture numbers and dates are formatted in. Name one the interface language is not, or the test watches nothing: on a Russian machine the two start equal.</param>
    public UiCulture(string name, string? formatting = null)
    {
        var culture = CultureInfo.GetCultureInfo(name);

        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (formatting is not null)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(formatting);
        }
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _thread;
        CultureInfo.DefaultThreadCurrentUICulture = _newThreads;
        CultureInfo.CurrentCulture = _formatting;
    }
}

[CollectionDefinition(CultureBound.Name)]
public sealed class CultureBound
{
    public const string Name = "interface language";
}
