using Microsoft.Build.Locator;
using MSBuild.Dag.Cli;

MSBuildLocator.RegisterDefaults();
return await CliRunner.RunAsync(args);
