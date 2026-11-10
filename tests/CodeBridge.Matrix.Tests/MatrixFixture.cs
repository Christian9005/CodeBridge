namespace CodeBridge.Matrix.Tests;

/// <summary>Builds, validates, runs and exports every block on every board once, for all the tests of the matrix.</summary>
public sealed class MatrixFixture : IAsyncLifetime
{
    internal IReadOnlyList<Cell> Cells { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var cells = Matrix.Cells().ToList();
        foreach (var cell in cells)
            Matrix.Validate(cell);

        // Every cell has its own virtual board, so they can run side by side. A block that waits (the timer) is cut after a second: it started and did not crash.
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(cells.Where(c => c.Valid).Select(async cell =>
        {
            await gate.WaitAsync();
            try
            {
                await Matrix.RunAsync(cell, TimeSpan.FromSeconds(1));
                Matrix.Export(cell);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var board in Matrix.Boards)
            Matrix.Compile(cells.Where(c => c.Board.Id == board.Id).ToList());

        Cells = cells;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
