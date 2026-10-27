using System;
using System.IO;
using System.Text;

namespace NuclearSatelliteMap
{
    // Little-endian .nosat container written by tools/build_satellite_data.py:
    //   "NOSATMP1", u32 headerSize, u32 version, u32 format, u32 width, u32 height, u32 mipCount,
    //   f32 mapWidthMeters, f32 mapHeightMeters, u32 nativeWidth, u32 nativeHeight, u64 dataLength,
    //   version 2+: u32 compression (0 none, 1 raw RFC 1951 deflate), u64 storedLength,
    //   version 3+: u32 tileColumns, u32 tileRows,
    //   then UTF-8 strings (u16 length) mapId, displayName, source, zero padding to headerSize.
    // Width/height describe the whole image; mipCount is the full chain of one tile. The
    // uncompressed data holds the tiles row-major (row 0 north, column 0 west), each a BC3 mip
    // chain in Unity raw layout: mip 0 first, each mip's bottom row first. Versions 1 and 2 are
    // one tile; version 1 is uncompressed.
    internal sealed class SatelliteMapFile
    {
        internal const string Magic = "NOSATMP1";
        internal const int FormatBc3 = 1;
        internal const int CompressionNone = 0, CompressionDeflate = 1;

        internal string Path { get; private set; } = "";
        internal int HeaderSize { get; private set; }
        internal int Version { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal int MipCount { get; private set; }
        internal float MapWidthMeters { get; private set; }
        internal float MapHeightMeters { get; private set; }
        internal int NativeWidth { get; private set; }
        internal int NativeHeight { get; private set; }
        internal long DataLength { get; private set; }
        internal int Compression { get; private set; }
        internal long StoredLength { get; private set; }
        internal int TileColumns { get; private set; } = 1;
        internal int TileRows { get; private set; } = 1;
        internal int TileWidth => Width / TileColumns;
        internal int TileHeight => Height / TileRows;
        internal long TileDataLength => SatelliteBlocks.Bc3Size(TileWidth, TileHeight, MipCount);
        internal string MapId { get; private set; } = "";
        internal string DisplayName { get; private set; } = "";
        internal string Source { get; private set; } = "";

        internal float MetersPerPixel => MapWidthMeters / Width;

        internal bool Matches(float mapWidthMeters, float mapHeightMeters, int nativeWidth, int nativeHeight) =>
            Math.Abs(MapWidthMeters - mapWidthMeters) < 1f && Math.Abs(MapHeightMeters - mapHeightMeters) < 1f &&
            NativeWidth == nativeWidth && NativeHeight == nativeHeight;

        internal static SatelliteMapFile Read(Stream stream, string path)
        {
            var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic) throw new InvalidDataException("Not a satellite map file.");
            var file = new SatelliteMapFile { Path = path, HeaderSize = reader.ReadInt32(), Version = reader.ReadInt32() };
            int format = reader.ReadInt32();
            if (file.Version < 1 || file.Version > 3) throw new InvalidDataException($"Unsupported satellite map version {file.Version}.");
            if (format != FormatBc3) throw new InvalidDataException($"Unsupported satellite map format {format}.");
            file.Width = reader.ReadInt32(); file.Height = reader.ReadInt32(); file.MipCount = reader.ReadInt32();
            file.MapWidthMeters = reader.ReadSingle(); file.MapHeightMeters = reader.ReadSingle();
            file.NativeWidth = reader.ReadInt32(); file.NativeHeight = reader.ReadInt32();
            file.DataLength = reader.ReadInt64();
            if (file.Version >= 2)
            {
                file.Compression = reader.ReadInt32();
                file.StoredLength = reader.ReadInt64();
            }
            else
            {
                file.StoredLength = file.DataLength;
            }
            if (file.Version >= 3)
            {
                file.TileColumns = reader.ReadInt32();
                file.TileRows = reader.ReadInt32();
            }
            file.MapId = ReadString(reader); file.DisplayName = ReadString(reader); file.Source = ReadString(reader);
            if (file.Compression != CompressionNone && file.Compression != CompressionDeflate)
                throw new InvalidDataException($"Unsupported satellite map compression {file.Compression}.");
            if (file.TileColumns < 1 || file.TileRows < 1 || file.TileColumns > 16 || file.TileRows > 16)
                throw new InvalidDataException($"Invalid tile grid {file.TileColumns}x{file.TileRows}.");
            if (file.Width <= 0 || file.Height <= 0 || file.Width % file.TileColumns != 0 || file.Height % file.TileRows != 0 ||
                file.TileWidth % 4 != 0 || file.TileHeight % 4 != 0)
                throw new InvalidDataException($"Invalid texture size {file.Width}x{file.Height} for {file.TileColumns}x{file.TileRows} tiles.");
            if (file.MipCount != SatelliteBlocks.FullMipCount(file.TileWidth, file.TileHeight))
                throw new InvalidDataException($"Expected a full mip chain, found {file.MipCount} levels.");
            if (file.DataLength != file.TileDataLength * file.TileColumns * file.TileRows)
                throw new InvalidDataException($"Data length {file.DataLength} does not match the mip chain.");
            if (file.Compression == CompressionNone ? file.StoredLength != file.DataLength : file.StoredLength <= 0)
                throw new InvalidDataException($"Invalid stored length {file.StoredLength}.");
            if (file.HeaderSize < stream.Position || file.HeaderSize % 16 != 0) throw new InvalidDataException("Invalid header size.");
            if (stream.CanSeek && stream.Length != file.HeaderSize + file.StoredLength)
                throw new InvalidDataException($"File is {stream.Length} bytes, expected {file.HeaderSize + file.StoredLength}.");
            return file;
        }

        // Fills destination (DataLength bytes) from a stream over the whole file, inflating if needed.
        internal void ReadPayload(Stream file, Span<byte> destination)
        {
            if (destination.Length != DataLength) throw new ArgumentException("Destination must hold the uncompressed payload.");
            file.Position = HeaderSize;
            var payload = Compression == CompressionDeflate
                ? new System.IO.Compression.DeflateStream(file, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true)
                : file;
            try
            {
                int offset = 0;
                while (offset < destination.Length)
                {
                    int read = payload.Read(destination.Slice(offset));
                    if (read <= 0) throw new EndOfStreamException($"Satellite map data ended after {offset} of {destination.Length} bytes.");
                    offset += read;
                }
                if (payload.Read(new byte[1], 0, 1) != 0) throw new InvalidDataException("Satellite map data is longer than declared.");
            }
            finally
            {
                if (payload != file) payload.Dispose();
            }
        }

        internal static SatelliteMapFile Read(string path)
        {
            using var stream = File.OpenRead(path);
            return Read(stream, path);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }
    }
}
