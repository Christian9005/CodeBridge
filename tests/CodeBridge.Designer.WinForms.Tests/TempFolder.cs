namespace CodeBridge.Designer.WinForms.Tests;

/// <summary>A folder that exists for one test and is always removed afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cb-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relativePath, string? content = null)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        if (content is not null)
            System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // a watcher may still hold the folder for a moment; the test run is over anyway
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
