namespace CodeBridge.Flow;

/// <summary>
/// Registry of block definitions available to a visual flow editor.
/// </summary>
public sealed class FlowBlockCatalog
{
    private readonly Dictionary<string, FlowBlockDefinition> _blocks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FlowBlockDefinition> Blocks => _blocks.Values;

    public FlowBlockCatalog Register(FlowBlockDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.Type))
            throw new ArgumentException("Block type is required.", nameof(definition));

        if (!_blocks.TryAdd(definition.Type, definition))
            throw new InvalidOperationException($"Block type '{definition.Type}' is already registered.");

        return this;
    }

    public FlowBlockDefinition Get(string type)
    {
        if (TryGet(type, out var definition))
            return definition;

        throw new KeyNotFoundException($"Block type '{type}' is not registered.");
    }

    public bool TryGet(string? type, out FlowBlockDefinition definition)
    {
        // a file can say "Type": null; that is an unknown block, not a reason to throw
        if (type is null)
        {
            definition = null!;
            return false;
        }

        return _blocks.TryGetValue(type, out definition!);
    }
}

