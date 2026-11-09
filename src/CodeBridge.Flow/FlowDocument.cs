namespace CodeBridge.Flow;

/// <summary>
/// Serializable visual flow document shared by WinForms, Blazor, and MAUI surfaces.
/// </summary>
public sealed class FlowDocument
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled Flow";
    public string BoardId { get; set; } = BuiltInBoardProfiles.Default.Id;
    public int Version { get; set; } = 1;

    // A file can say "Nodes": null. That is an empty list, never a null that fails later in some other part of the SDK.
    private List<FlowNode> _nodes = [];
    private List<FlowConnection> _connections = [];

    public List<FlowNode> Nodes { get => _nodes; init => _nodes = value ?? []; }
    public List<FlowConnection> Connections { get => _connections; init => _connections = value ?? []; }
}

public sealed class FlowNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Type { get; init; }
    public FlowPosition Position { get; set; } = new(0, 0);

    private Dictionary<string, object?> _parameters = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, object?> Parameters { get => _parameters; init => _parameters = value ?? new(StringComparer.OrdinalIgnoreCase); }
}

public sealed class FlowConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FromNodeId { get; init; }
    public required string FromPort { get; init; }
    public required string ToNodeId { get; init; }
    public required string ToPort { get; init; }
}

public readonly record struct FlowPosition(double X, double Y);
