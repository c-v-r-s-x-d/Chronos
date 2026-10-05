using System.Text.RegularExpressions;
using Chronos.App.ViewModels;
using Chronos.Core.Sessions;

namespace Chronos.App.Tests;

/// <summary>The length a user can type is the length the service keeps. The interface cannot reference the domain, so it holds both numbers and this keeps them true.</summary>
public sealed class SessionBoundsTests
{
    [Fact]
    public void TheShortestSessionIsTheServices()
    {
        Assert.Equal(SessionLimits.MinSessionDuration, TimeSpan.FromMinutes(SetupViewModel.MinMinutes));
    }

    [Fact]
    public void TheLongestSessionIsTheServices()
    {
        Assert.Equal(SessionLimits.MaxSessionDuration, TimeSpan.FromMinutes(SetupViewModel.MaxMinutes));
    }

    [Fact]
    public void TheBoxAllowsExactlyTheSameRange()
    {
        var page = File.ReadAllText(Repo.App("Views", "SessionSetupPage.axaml"));
        var box = Regex.Match(page, @"<NumericUpDown Value=""\{Binding Minutes\}""[^>]*>").Value;

        Assert.Contains($@"Minimum=""{SetupViewModel.MinMinutes}""", box);
        Assert.Contains($@"Maximum=""{SetupViewModel.MaxMinutes}""", box);
    }

    [Fact]
    public void TheLengthBeforeAnyConfigurationIsInsideTheRange()
    {
        Assert.InRange(SetupViewModel.DefaultMinutes, SetupViewModel.MinMinutes, SetupViewModel.MaxMinutes);
    }
}
