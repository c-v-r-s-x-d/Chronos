using System.Security.Principal;

namespace Chronos.Service.Tests;

/// <summary>A fact that needs administrator rights; it skips rather than fails without them.</summary>
public sealed class RequiresElevationAttribute : FactAttribute
{
    /// <param name="because">What the rights are needed for.</param>
    public RequiresElevationAttribute(string because = "open a filter engine session")
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "Needs administrator rights to " + because + ".";
        }
    }
}
