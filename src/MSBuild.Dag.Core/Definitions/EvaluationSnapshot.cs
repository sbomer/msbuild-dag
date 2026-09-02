namespace MSBuild.Dag.Core;

public abstract class StateInitialization
{
    public abstract StateLocation Location { get; }

    public abstract InitialValue InitialValue { get; }
}

public sealed class StateInitialization<T>(
    StateLocation<T> location,
    T content) : StateInitialization
{
    private readonly InitialValue<T> _initialValue =
        new(new Value<T>(), content);

    public override StateLocation<T> Location { get; } = location;

    public override InitialValue<T> InitialValue => _initialValue;
}

public sealed partial class EvaluationSnapshot
{
    private readonly Dictionary<StateLocation, StateInitialization>
        _initializations =
            new(ReferenceEqualityComparer.Instance);

    public EvaluationSnapshot(
        IReadOnlyList<StateInitialization> initializations)
    {
        ArgumentNullException.ThrowIfNull(initializations);

        Initializations = initializations.ToArray();

        foreach (var initialization in Initializations)
        {
            ArgumentNullException.ThrowIfNull(initialization);

            if (!_initializations.TryAdd(
                initialization.Location,
                initialization))
            {
                throw new ArgumentException(
                    "A state location cannot be initialized more than once.",
                    nameof(initializations));
            }
        }
    }

    public IReadOnlyList<StateInitialization> Initializations { get; }
}
