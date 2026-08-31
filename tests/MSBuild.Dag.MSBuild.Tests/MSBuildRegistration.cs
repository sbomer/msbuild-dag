using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace MSBuild.Dag.MSBuild.Tests;

internal static class MSBuildRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        MSBuildLocator.RegisterDefaults();
    }
}
