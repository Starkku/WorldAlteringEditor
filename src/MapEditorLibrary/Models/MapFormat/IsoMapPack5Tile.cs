using System.Buffers.Binary;

namespace MapEditorLibrary.Models.MapFormat;

/// <summary>
/// Low-level cell class.
/// </summary>
public class IsoMapPack5Tile
{
    public const int Size = 11;

    public IsoMapPack5Tile() { }

    public IsoMapPack5Tile(Span<byte> data)
    {
        X = BinaryPrimitives.ReadInt16LittleEndian(data);
        Y = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(2));
        TileIndex = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4));
        SubTileIndex = data[8];
        Level = data[9];
        IceGrowth = data[10];
    }

    public short X { get; set; }
    public short Y { get; set; }
    public int TileIndex { get; set; }
    public byte SubTileIndex { get; set; }
    public byte Level { get; set; }
    public byte IceGrowth { get; set; }
}
