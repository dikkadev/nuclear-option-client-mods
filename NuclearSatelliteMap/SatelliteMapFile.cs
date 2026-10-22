using System;
using System.IO;
using System.Text;

namespace NuclearSatelliteMap
{
    // Little-endian .nosat container written by tools/build_satellite_data.py:
    //   "NOSATMP1", u32 headerSize, u32 version, u32 format, u32 width, u32 height, u32 mipCount,
    //   f32 mapWidthMeters, f32 mapHeightMeters, u32 nativeWidth, u32 nativeHeight, u64 dataLength,
    //   then UTF-8 strings (u16 length) mapId, displayName, source, zero padding to headerSize.
    // Data is the full BC3 mip chain in Unity raw layout: mip 0 first, each mip's bottom row first.
    internal sealed class SatelliteMapFile
    {
        internal const string Magic = "NOSATMP1";
        internal const int Version = 1;
        internal const int FormatBc3 = 1;

        internal string Path { get; private set; } = "";
        internal int HeaderSize { get; private set; }
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal int MipCount { get; private set; }
        internal float MapWidthMeters { get; private set; }
        internal float MapHeightMeters { get; private set; }
        internal int NativeWidth { get; private set; }
        internal int NativeHeight { get; private set; }
        internal long DataLength { get; private set; }
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
            var file = new SatelliteMapFile { Path = path, HeaderSize = reader.ReadInt32() };
            int version = reader.ReadInt32(), format = reader.ReadInt32();
            if (version != Version) throw new InvalidDataException($"Unsupported satellite map version {version}.");
            if (format != FormatBc3) throw new InvalidDataException($"Unsupported satellite map format {format}.");
            file.Width = reader.ReadInt32(); file.Height = reader.ReadInt32(); file.MipCount = reader.ReadInt32();
            file.MapWidthMeters = reader.ReadSingle(); file.MapHeightMeters = reader.ReadSingle();
            file.NativeWidth = reader.ReadInt32(); file.NativeHeight = reader.ReadInt32();
            file.DataLength = reader.ReadInt64();
            file.MapId = ReadString(reader); file.DisplayName = ReadString(reader); file.Source = ReadString(reader);
            if (file.Width <= 0 || file.Height <= 0 || file.Width % 4 != 0 || file.Height % 4 != 0)
                throw new InvalidDataException($"Invalid texture size {file.Width}x{file.Height}.");
            if (file.MipCount != SatelliteBlocks.FullMipCount(file.Width, file.Height))
                throw new InvalidDataException($"Expected a full mip chain, found {file.MipCount} levels.");
            if (file.DataLength != SatelliteBlocks.Bc3Size(file.Width, file.Height, file.MipCount))
                throw new InvalidDataException($"Data length {file.DataLength} does not match the mip chain.");
            if (file.HeaderSize < stream.Position || file.HeaderSize % 16 != 0) throw new InvalidDataException("Invalid header size.");
            if (stream.CanSeek && stream.Length != file.HeaderSize + file.DataLength)
                throw new InvalidDataException($"File is {stream.Length} bytes, expected {file.HeaderSize + file.DataLength}.");
            return file;
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
