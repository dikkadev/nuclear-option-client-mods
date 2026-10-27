using System.Buffers.Binary;
using System.Text;
using NuclearSatelliteMap;

internal static class SatelliteMapChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("satellite full mip counts and BC3 sizes", () => {
            // Independent sums: base level plus all smaller levels, each at least one block.
            foreach (var (width, height, mips, bytes) in new[] {
                (8192, 8192, 14, 89478512L), (16384, 8192, 15, 178957008L),
                (1, 1, 1, 16L), (1, 8, 4, 80L), (8, 1, 4, 80L),
                (2, 3, 2, 32L), (3, 9, 4, 96L), (8, 4, 4, 80L) }) {
                Equal(mips, SatelliteBlocks.FullMipCount(width, height));
                Equal(bytes, SatelliteBlocks.Bc3Size(width, height, mips));
            }
            Equal(67108864L, SatelliteBlocks.Bc3Size(8192, 8192, 1));
            Equal(134217728L, SatelliteBlocks.Bc3Size(16384, 8192, 1));
        });
        test("satellite file accepts synthetic full mip stream and metadata", () => {
            using var stream = new MemoryStream(FileBytes());
            var file = SatelliteMapFile.Read(stream, "synthetic.nosat");
            Equal("synthetic.nosat", file.Path); Equal(128, file.HeaderSize);
            Equal(8, file.Width); Equal(4, file.Height); Equal(4, file.MipCount); Equal(80L, file.DataLength);
            Equal(81920f, file.MapWidthMeters); Equal(40960f, file.MapHeightMeters);
            Equal(2048, file.NativeWidth); Equal(1024, file.NativeHeight);
            Equal("map-test", file.MapId); Equal("Synthetic map", file.DisplayName); Equal("fixture", file.Source);
            Equal(10240f, file.MetersPerPixel);
            Require(stream.CanRead, "Read disposed its caller's stream.");
            Require(file.Matches(81920.5f, 40960.5f, 2048, 1024), "Matching metadata rejected.");
            Require(!file.Matches(81921f, 40960f, 2048, 1024), "Meter tolerance boundary accepted.");
            Require(!file.Matches(81920f, 40960f, 2049, 1024), "Native size mismatch accepted.");
        });
        foreach (var (name, offset, value) in new[] {
            ("magic", 0, 0), ("version", 12, 4), ("format", 16, 2),
            ("zero width", 20, 0), ("negative height", 24, -4), ("unaligned width", 20, 6),
            ("missing mips", 28, 3), ("extra mips", 28, 5), ("zero mips", 28, 0),
            ("declared data length", 48, 96), ("size/payload mismatch", 20, 12),
            ("short header", 8, 64), ("unaligned header", 8, 129) })
            test("satellite file rejects " + name, () => {
                var bytes = FileBytes();
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
                Throws<InvalidDataException>(() => SatelliteMapFile.Read(new MemoryStream(bytes), "fixture"));
            });
        test("satellite file rejects truncated and trailing payloads", () => {
            var bytes = FileBytes();
            Throws<InvalidDataException>(() => SatelliteMapFile.Read(new MemoryStream(bytes[..^1]), "fixture"));
            Throws<InvalidDataException>(() => SatelliteMapFile.Read(new MemoryStream(bytes.Concat(new byte[1]).ToArray()), "fixture"));
            Throws<EndOfStreamException>(() => SatelliteMapFile.Read(new MemoryStream(bytes[..30]), "fixture"));
        });
        test("satellite version 1 payload reads uncompressed bytes exactly", () => {
            var bytes = FileBytes(Payload());
            using var stream = new MemoryStream(bytes);
            var file = SatelliteMapFile.Read(stream, "v1");
            Equal(1, file.Version); Equal(SatelliteMapFile.CompressionNone, file.Compression); Equal(80L, file.StoredLength);
            var data = new byte[80];
            file.ReadPayload(stream, data);
            Require(data.SequenceEqual(Payload()), "Uncompressed payload changed.");
        });
        test("satellite version 2 deflate payload inflates to the declared data", () => {
            var bytes = DeflateFileBytes(Deflate(Payload()));
            using var stream = new MemoryStream(bytes);
            var file = SatelliteMapFile.Read(stream, "v2");
            Equal(2, file.Version); Equal(SatelliteMapFile.CompressionDeflate, file.Compression);
            Equal(80L, file.DataLength); Equal((long)Deflate(Payload()).Length, file.StoredLength);
            Equal("map-test", file.MapId); Equal("fixture", file.Source);
            var data = new byte[80];
            file.ReadPayload(stream, data);
            Require(data.SequenceEqual(Payload()), "Inflated payload changed.");
            Throws<ArgumentException>(() => file.ReadPayload(stream, new byte[79]));
        });
        test("satellite version 2 rejects unknown compression and wrong stored sizes", () => {
            var bytes = DeflateFileBytes(Deflate(Payload()));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), 2);
            Throws<InvalidDataException>(() => SatelliteMapFile.Read(new MemoryStream(bytes), "fixture"));
            bytes = DeflateFileBytes(Deflate(Payload()));
            Throws<InvalidDataException>(() => SatelliteMapFile.Read(new MemoryStream(bytes.Concat(new byte[1]).ToArray()), "fixture"));
            bytes = DeflateFileBytes(Deflate(Payload()));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), 0);
            Throws<InvalidDataException>(() => SatelliteMapFile.Read(new MemoryStream(bytes), "fixture"));
        });
        test("satellite version 2 rejects short and overlong inflated payloads", () => {
            foreach (var content in new[] { Payload()[..79], Payload().Concat(new byte[1]).ToArray() }) {
                using var stream = new MemoryStream(DeflateFileBytes(Deflate(content)));
                var file = SatelliteMapFile.Read(stream, "fixture");
                Throws<Exception>(() => file.ReadPayload(stream, new byte[80]));
            }
        });
        RunTiled(test);
    }

    internal static void RunTiled(Action<string, Action> test)
    {
        test("satellite half detail skips exactly the first mip of each tile", () => {
            foreach (var (width, height) in new[] { (16384, 16384), (8192, 4096), (8, 8), (4, 8) }) {
                int mips = SatelliteBlocks.FullMipCount(width, height);
                Equal(SatelliteBlocks.Bc3Size(width, height, mips) - SatelliteBlocks.Bc3Size(width, height, 1),
                    SatelliteBlocks.Bc3Size(width / 2, height / 2, mips - 1));
            }
        });
        test("satellite version 3 reads a 2x1 tile grid with per-tile mip chains", () => {
            // Two 8x8 tiles: whole image 16x8, four mips per tile.
            long tileBytes = SatelliteBlocks.Bc3Size(8, 8, 4);
            var payload = Enumerable.Range(0, (int)(2 * tileBytes)).Select(i => (byte)(i % 253)).ToArray();
            using var stream = new MemoryStream(TiledFileBytes(16, 8, 4, 2, 1, Deflate(payload), payload.Length));
            var file = SatelliteMapFile.Read(stream, "tiled");
            Equal(3, file.Version); Equal(2, file.TileColumns); Equal(1, file.TileRows);
            Equal(8, file.TileWidth); Equal(8, file.TileHeight); Equal(tileBytes, file.TileDataLength);
            Equal(16, file.Width); Equal(81920f / 16, file.MetersPerPixel);
            var data = new byte[file.DataLength];
            file.ReadPayload(stream, data);
            Require(data.SequenceEqual(payload), "Tiled payload changed.");
        });
        foreach (var (name, width, height, mips, columns, rows) in new[] {
            ("mip count of the whole image", 16, 8, 5, 2, 1),
            ("uneven columns", 12, 8, 4, 2, 1),
            ("zero columns", 16, 8, 4, 0, 1),
            ("too many rows", 16, 8, 4, 2, 17),
            ("tile not a block multiple", 12, 4, 2, 2, 1) })
            test("satellite version 3 rejects " + name, () => {
                long tile = columns > 0 && rows > 0 && width % columns == 0 && height % rows == 0
                    ? SatelliteBlocks.Bc3Size(width / columns, height / rows, mips) : 16;
                long total = tile * Math.Max(1, columns) * Math.Max(1, rows);
                var payload = new byte[total];
                Throws<InvalidDataException>(() => SatelliteMapFile.Read(
                    new MemoryStream(TiledFileBytes(width, height, mips, columns, rows, Deflate(payload), total)), "fixture"));
            });
    }

    private static byte[] TiledFileBytes(int width, int height, int mips, int columns, int rows, byte[] stored, long dataLength)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("NOSATMP1"));
        writer.Write(128); writer.Write(3); writer.Write(1);
        writer.Write(width); writer.Write(height); writer.Write(mips);
        writer.Write(81920f); writer.Write(40960f); writer.Write(2048); writer.Write(1024); writer.Write(dataLength);
        writer.Write(SatelliteMapFile.CompressionDeflate); writer.Write((long)stored.Length);
        writer.Write(columns); writer.Write(rows);
        foreach (string value in new[] { "map-test", "Synthetic map", "fixture" }) {
            var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        writer.Write(new byte[128 - stream.Position]); writer.Write(stored);
        return stream.ToArray();
    }

    private static byte[] Payload() => Enumerable.Range(0, 80).Select(i => (byte)(i * 7 % 251)).ToArray();

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    private static byte[] DeflateFileBytes(byte[] stored)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("NOSATMP1"));
        writer.Write(128); writer.Write(2); writer.Write(1);
        writer.Write(8); writer.Write(4); writer.Write(4);
        writer.Write(81920f); writer.Write(40960f); writer.Write(2048); writer.Write(1024); writer.Write(80L);
        writer.Write(SatelliteMapFile.CompressionDeflate); writer.Write((long)stored.Length);
        foreach (string value in new[] { "map-test", "Synthetic map", "fixture" }) {
            var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        writer.Write(new byte[128 - stream.Position]); writer.Write(stored);
        return stream.ToArray();
    }

    private static byte[] FileBytes(byte[]? payload = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("NOSATMP1"));
        writer.Write(128); writer.Write(1); writer.Write(1);
        writer.Write(8); writer.Write(4); writer.Write(4);
        writer.Write(81920f); writer.Write(40960f); writer.Write(2048); writer.Write(1024); writer.Write(80L);
        foreach (string value in new[] { "map-test", "Synthetic map", "fixture" }) {
            var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        writer.Write(new byte[128 - stream.Position]); writer.Write(payload ?? new byte[80]);
        return stream.ToArray();
    }
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, found {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
