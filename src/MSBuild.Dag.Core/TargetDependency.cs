namespace MSBuild.Dag.Core;

public sealed record TargetDependency(Target Prerequisite, Target Dependent);
