using MapEditorLibrary.GameMath;
using MapEditorLibrary.Models;
using MapEditorLibrary.Mutations;

namespace MapEditorMCP.MCPMutations;

/// <summary>
/// Directly changes the raw height level of one or more map cells.
/// </summary>
public sealed class SetCellsHeightMutation : Mutation
{
    public SetCellsHeightMutation(IMutationTarget mutationTarget, List<Point2D> cellCoords, byte height)
        : base(mutationTarget)
    {
        this.cellCoords = cellCoords ?? throw new ArgumentNullException(nameof(cellCoords));
        if (cellCoords.Count == 0)
            throw new ArgumentException("At least one cell coordinate must be provided.", nameof(cellCoords));

        this.height = height;
    }

    private readonly struct OriginalHeight
    {
        public OriginalHeight(Point2D cellCoords, byte height)
        {
            CellCoords = cellCoords;
            Height = height;
        }

        public Point2D CellCoords { get; }
        public byte Height { get; }
    }

    private readonly List<Point2D> cellCoords;
    private readonly byte height;
    private readonly List<OriginalHeight> originalHeights = new();

    public override string GetDisplayString()
    {
        return $"Set raw height on {cellCoords.Count} map cell(s) to level {height}";
    }

    public override void Perform()
    {
        originalHeights.Clear();

        foreach (Point2D coords in cellCoords)
        {
            MapTile mapTile = Map.GetTile(coords);
            if (mapTile == null)
                throw new InvalidOperationException($"Cell {coords} does not exist.");

            originalHeights.Add(new OriginalHeight(coords, mapTile.Level));
            SetHeight(mapTile, height);
        }

        MutationTarget.InvalidateMap();
    }

    public override void Undo()
    {
        for (int i = originalHeights.Count - 1; i >= 0; i--)
        {
            OriginalHeight original = originalHeights[i];
            MapTile mapTile = Map.GetTile(original.CellCoords);
            if (mapTile == null)
                throw new InvalidOperationException($"Cell {original.CellCoords} does not exist.");

            SetHeight(mapTile, original.Height);
        }

        MutationTarget.InvalidateMap();
    }

    private void SetHeight(MapTile mapTile, byte newHeight)
    {
        mapTile.Level = newHeight;
        RefreshCellLighting(mapTile);
    }
}
