using MapEditorLibrary.CCEngine.TileData;
using MapEditorLibrary.Configuration;
using MapEditorLibrary.GameMath;
using MapEditorLibrary.Models;

namespace MapEditorLibrary.Mutations.Classes;

/// <summary>
/// A mutation that places a terrain tile on the map.
/// </summary>
public class PlaceTerrainTileMutation : Mutation, ICheckableMutation
{
    public PlaceTerrainTileMutation(IMutationTarget mutationTarget, Point2D targetCellCoords, ITileImage tile, int heightOffset, int eventID)
        : this(mutationTarget, targetCellCoords, tile, heightOffset, mutationTarget.BrushSize,
            mutationTarget.AutoLATEnabled, mutationTarget.OnlyPaintOnClearGround)
    {
        EventID = eventID;
    }

    public PlaceTerrainTileMutation(IMutationTarget mutationTarget, Point2D targetCellCoords, ITileImage tile, int heightOffset,
        BrushSize brushSize, bool autoLATEnabled, bool onlyPaintOnClearGround) : base(mutationTarget)
    {
        TargetCellCoords = targetCellCoords;
        Tile = tile;
        HeightOffset = heightOffset;
        BrushSize = brushSize;
        AutoLATEnabled = autoLATEnabled;
        OnlyPaintOnClearGround = onlyPaintOnClearGround;
    }

    public Point2D TargetCellCoords { get; }
    public ITileImage Tile { get; }
    public int HeightOffset { get; }
    public BrushSize BrushSize { get; }
    public bool AutoLATEnabled { get; }
    public bool OnlyPaintOnClearGround { get; }

    private List<OriginalCellTerrainData> undoData;
    private HashSet<Point2D> undoDataCellCoords;

    public bool ShouldPerform() => true;

    public override string GetDisplayString()
    {
        var tileSet = MutationTarget.TheaterGraphicsData.Theater.TileSets[Tile.TileSetId];
        return string.Format(Translate(this, "DisplayString", 
            "Place terrain tile of TileSet {0} at {1} with a brush size of {2}"),
                tileSet.TranslatedName, TargetCellCoords, BrushSize);
    }

    private void AddCellToUndoData(Point2D cellCoords)
    {
        if (!undoDataCellCoords.Add(cellCoords))
            return;

        var mapTile = Map.GetTileOrFail(cellCoords);

        undoData.Add(new OriginalCellTerrainData(cellCoords, mapTile.TileIndex, mapTile.SubTileIndex, mapTile.Level));
    }

    public override void Perform()
    {
        undoData = new List<OriginalCellTerrainData>(Tile.SubTileCount * BrushSize.Width * BrushSize.Height);
        undoDataCellCoords = new HashSet<Point2D>();

        MapTile originCell = MutationTarget.Map.GetTile(TargetCellCoords);
        int originLevel = -1;

        // First, look up the lowest point within the tile area for origin level
        // Only use a 1x1 brush size for this (meaning no brush at all)
        // so users can use larger brush sizes to "paint height"
        for (int i = 0; i < Tile.SubTileCount; i++)
        {
            Point2D? subTileOffset = Tile.GetSubTileCoordOffset(i);

            if (subTileOffset == null)
                continue;

            var mapTile = MutationTarget.Map.GetTile(TargetCellCoords + subTileOffset.Value);

            if (mapTile != null)
            {
                var existingTile = Map.TheaterInstance.GetTile(mapTile.TileIndex).GetSubTile(mapTile.SubTileIndex);

                int cellLevel = mapTile.Level;

                // Allow replacing back cliffs
                if (existingTile.TmpImage.Height == Tile.GetSubTile(i).TmpImage.Height)
                    cellLevel -= existingTile.TmpImage.Height;

                if (originLevel < 0 || cellLevel < originLevel)
                    originLevel = cellLevel;
            }
        }

        originLevel += HeightOffset;
        if (originLevel < 0)
            originLevel = 0;

        // Place the terrain
        BrushSize.DoForBrushSize(offset =>
        {
            for (int i = 0; i < Tile.SubTileCount; i++)
            {
                Point2D? subTileOffset = Tile.GetSubTileCoordOffset(i);

                if (subTileOffset == null)
                    continue;

                int cx = TargetCellCoords.X + (offset.X * Tile.Width) + i % Tile.Width;
                int cy = TargetCellCoords.Y + (offset.Y * Tile.Height) + i / Tile.Width;

                var mapTile = MutationTarget.Map.GetTile(TargetCellCoords + new Point2D(offset.X * Tile.Width, offset.Y * Tile.Height) + subTileOffset.Value);
                if (mapTile != null && (!OnlyPaintOnClearGround || mapTile.IsClearGround()))
                {
                    AddCellToUndoData(mapTile.CoordsToPoint());
                    mapTile.ChangeTileIndex(Tile.TileID, (byte)i);
                    mapTile.Level = (byte)Math.Min(originLevel + Tile.GetSubTile(i).TmpImage.Height, Constants.MaxMapHeightLevel);
                    RefreshCellLighting(mapTile);
                }
            }
        });

        // Apply autoLAT if necessary
        if (AutoLATEnabled)
        {
            ApplyAutoLATForTilePlacement(Tile, BrushSize, TargetCellCoords, AddCellToUndoData);
        }

        MutationTarget.AddRefreshPoint(TargetCellCoords, Math.Max(Tile.Width, Tile.Height) * Math.Max(BrushSize.Width, BrushSize.Height));
    }

    public override void Undo()
    {
        for (int i = 0; i < undoData.Count; i++)
        {
            OriginalCellTerrainData originalTerrainData = undoData[i];

            var mapCell = MutationTarget.Map.GetTile(originalTerrainData.CellCoords);
            if (mapCell != null)
            {
                mapCell.ChangeTileIndex(originalTerrainData.TileIndex, originalTerrainData.SubTileIndex);
                mapCell.Level = originalTerrainData.HeightLevel;
                RefreshCellLighting(mapCell);
            }
        }

        MutationTarget.AddRefreshPoint(TargetCellCoords);
    }
}
