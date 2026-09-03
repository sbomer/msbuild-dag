namespace MSBuild.Dag.MSBuild;

public sealed record ValueSymbol(
    string Name,
    int Version)
{
    public override string ToString() => $"{Name}#{Version}";
}
