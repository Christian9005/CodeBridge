using System.Text.Json;
using System.Text.Json.Serialization;
using CodeBridge.Flow;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;

namespace CodeBridge.WebEditor;

/// <summary>The flow being edited, its file, and what the page needs to know about it. One session per server: the page is the only editor.</summary>
internal sealed class EditorSession
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private FlowDocument _document;

    public EditorSession(string path, string? boardId)
    {
        Path = System.IO.Path.GetFullPath(path);
        _document = File.Exists(Path)
            ? FlowDocumentJson.Deserialize(File.ReadAllText(Path))
            : new FlowDocument { Name = System.IO.Path.GetFileNameWithoutExtension(Path) };

        if (boardId is not null)
            _document.BoardId = (BuiltInBoardProfiles.FindById(boardId) ?? throw new InvalidOperationException($"Unknown board '{boardId}'.")).Id;
    }

    /// <summary>The only file the page can write: the one the server was started with.</summary>
    public string Path { get; }

    public FlowDocument Document
    {
        get
        {
            lock (_gate)
                return _document;
        }
    }

    public BoardProfile Board => BuiltInBoardProfiles.FindById(Document.BoardId) ?? BuiltInBoardProfiles.Default;

    public string FlowJson()
    {
        lock (_gate)
            return FlowDocumentJson.Serialize(_document);
    }

    /// <summary>Replaces the flow with what the page sent and validates it. It is written to the file only when <paramref name="save"/> is set and the flow has no errors.</summary>
    public object Update(string json, bool save)
    {
        FlowDocument incoming;
        try
        {
            incoming = FlowDocumentJson.Deserialize(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new { valid = false, saved = false, issues = new[] { new { severity = "error", message = "The flow could not be read: " + ex.Message } } };
        }

        var profile = BuiltInBoardProfiles.FindById(incoming.BoardId) ?? BuiltInBoardProfiles.Default;
        var validation = new FlowValidator().Validate(incoming, BuiltInBlockCatalog.Create(profile));
        var saved = false;
        lock (_gate)
        {
            _document = incoming;
            if (save && validation.IsValid)
            {
                var temporary = Path + ".tmp";
                File.WriteAllText(temporary, FlowDocumentJson.Serialize(incoming));
                File.Move(temporary, Path, overwrite: true);
                saved = true;
            }
        }

        return new
        {
            valid = validation.IsValid,
            saved,
            issues = validation.Issues.Select(i => new { severity = i.Severity.ToString().ToLowerInvariant(), message = i.Message, nodeId = i.NodeId, connectionId = i.ConnectionId })
        };
    }

    public object Catalog() => Catalog(Board);

    public static object Catalog(BoardProfile board) => new
    {
        boardId = board.Id,
        displayName = board.DisplayName,
        blocks = BuiltInBlockCatalog.Create(board).Blocks
            .OrderBy(b => b.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(b => new
            {
                type = b.Type,
                displayName = b.DisplayName,
                category = b.Category,
                description = b.Description,
                ports = b.Ports.Select(p => new { name = p.Name, direction = p.Direction, valueKind = p.ValueKind, required = p.Required }),
                properties = b.Properties.Select(p => new
                {
                    name = p.Name,
                    valueKind = p.ValueKind,
                    required = p.Required,
                    isAdvanced = p.IsAdvanced,
                    defaultValue = p.DefaultValue,
                    options = p.Options?.Select(o => new { label = o.Label, value = o.Value, description = o.Description })
                })
            })
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    public static IEnumerable<object> Boards() => BuiltInBoardProfiles.All.Select(b => new { id = b.Id, displayName = b.DisplayName });
}
