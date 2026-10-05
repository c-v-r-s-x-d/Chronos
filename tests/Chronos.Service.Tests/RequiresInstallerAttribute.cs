namespace Chronos.Service.Tests;

/// <summary>A fact that reads the built MSI. It skips rather than fails when there is none: WiX is a global tool the repository does not install.</summary>
public sealed class RequiresInstallerAttribute : FactAttribute
{
    public RequiresInstallerAttribute()
    {
        if (InstallerPackage.Location is null)
        {
            Skip = "No MSI has been built. Run: "
                + "dotnet build src/Chronos.Installer/Chronos.Installer.wixproj -c Release";
        }
    }
}
