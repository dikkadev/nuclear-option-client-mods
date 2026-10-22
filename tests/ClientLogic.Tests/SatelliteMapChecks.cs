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
        test("satellite identity LUT detection checks every entry", () => {
            var colors = SatelliteBlocks.BuildColorLut(ColorAdjustment.Identity);
            var alpha = SatelliteBlocks.BuildAlphaLut(0, 1);
            Require(SatelliteBlocks.IsIdentity(colors, alpha), "Identity adjustment changed a LUT entry.");
            colors[^1]--;
            Require(!SatelliteBlocks.IsIdentity(colors, alpha), "Last color entry was ignored.");
            colors[^1]++;
            alpha[^1]--;
            Require(!SatelliteBlocks.IsIdentity(colors, alpha), "Last alpha entry was ignored.");
        });
        test("satellite reference decoder covers four/three color and eight/six alpha modes", () => {
            var block = Block(255, 0, 0xf800, 0x07e0);
            var pixels = Decode(block);
            Equal(new Pixel(255, 0, 0, 255), pixels[0]);
            Equal(new Pixel(0, 255, 0, 0), pixels[1]);
            Equal(new Pixel(170, 85, 0, 218), pixels[2]);
            Equal(new Pixel(85, 170, 0, 182), pixels[3]);
            block = Block(0, 255, 0x001f, 0xf800);
            pixels = Decode(block);
            Equal(new Pixel(0, 0, 255, 0), pixels[0]);
            Equal(new Pixel(255, 0, 0, 255), pixels[1]);
            Equal(new Pixel(127, 0, 127, 51), pixels[2]);
            Equal(new Pixel(0, 0, 0, 102), pixels[3]);
            Equal(0, pixels[6].A); Equal(255, pixels[7].A);
            var forced = Decode(block, forceFourColor: true, forceEightAlpha: true);
            Equal(new Pixel(85, 0, 170, 36), forced[2]);
            Equal(new Pixel(170, 0, 85, 72), forced[3]);
        });
        test("satellite identity preserves random canonical blocks byte for byte", () => {
            var random = new Random(47011);
            var colors = SatelliteBlocks.BuildColorLut(ColorAdjustment.Identity);
            var alpha = SatelliteBlocks.BuildAlphaLut(0, 1);
            for (int i = 0; i < 512; i++) {
                var original = RandomBlock(random, i);
                var changed = original.ToArray();
                SatelliteBlocks.Apply(changed, colors, alpha);
                Require(original.SequenceEqual(changed), $"Identity changed block {i}.");
            }
        });
        foreach (var (water, land) in new[] { (0f, 1f), (.2f, .8f), (.8f, .2f), (1f, 0f), (.4f, .4f), (0f, 0f), (1f, 1f) })
            test($"satellite affine alpha water={water} land={land}", () => {
                var random = new Random(47012);
                var colors = SatelliteBlocks.BuildColorLut(ColorAdjustment.Identity);
                var alpha = SatelliteBlocks.BuildAlphaLut(water, land);
                for (int i = 0; i < 512; i++) {
                    var block = RandomBlock(random, i);
                    var before = Decode(block);
                    SatelliteBlocks.Apply(block, colors, alpha);
                    Canonical(block);
                    var after = Decode(block);
                    for (int p = 0; p < 16; p++) {
                        // Two byte-rounding stages: LUT endpoints and BC3 interpolation.
                        Near(alpha[before[p].A], after[p].A, 2, $"alpha block {i} pixel {p}");
                        Equal(before[p] with { A = after[p].A }, after[p]);
                    }
                }
            });
        foreach (var (name, adjustment) in new[] {
            ("brightness", Adjustment(brightness: .65f)),
            ("contrast", Adjustment(contrast: .55f)),
            ("saturation", Adjustment(saturation: .35f)),
            ("grayscale", Adjustment(saturation: 0)),
            ("combined affine", Adjustment(brightness: .8f, contrast: .6f, saturation: .4f)),
            ("collapsed black", Adjustment(brightness: 0)) })
            test("satellite affine color " + name, () => {
                var random = new Random(47013);
                var colors = SatelliteBlocks.BuildColorLut(adjustment);
                var alpha = SatelliteBlocks.BuildAlphaLut(0, 1);
                for (int i = 0; i < 512; i++) {
                    var block = RandomBlock(random, i);
                    var before = Decode(block);
                    SatelliteBlocks.Apply(block, colors, alpha);
                    Canonical(block);
                    var after = Decode(block);
                    for (int p = 0; p < 16; p++) {
                        // Independent per-pixel affine reference, with gamma=1 and no clipping.
                        double r = ((before[p].R / 255.0 - .5) * adjustment.Contrast + .5) * adjustment.Brightness;
                        double g = ((before[p].G / 255.0 - .5) * adjustment.Contrast + .5) * adjustment.Brightness;
                        double b = ((before[p].B / 255.0 - .5) * adjustment.Contrast + .5) * adjustment.Brightness;
                        double luma = .2126 * r + .7152 * g + .0722 * b;
                        // Half a 565 step, bit expansion and integer interpolation rounding.
                        Near(255 * (luma + (r - luma) * adjustment.Saturation), after[p].R, 6, $"red {i}/{p}");
                        Near(255 * (luma + (g - luma) * adjustment.Saturation), after[p].G, 4, $"green {i}/{p}");
                        Near(255 * (luma + (b - luma) * adjustment.Saturation), after[p].B, 6, $"blue {i}/{p}");
                        Equal(before[p].A, after[p].A);
                    }
                }
            });
        test("satellite swapped and collapsed endpoints preserve canonical decoder agreement", () => {
            var identity = SatelliteBlocks.BuildColorLut(ColorAdjustment.Identity);
            var reversed = identity.Select(c => (ushort)(65535 - c)).ToArray();
            var constant = Enumerable.Repeat((ushort)0x6b4d, 65536).ToArray();
            foreach (var colors in new[] { reversed, constant })
            foreach (var alpha in new[] { SatelliteBlocks.BuildAlphaLut(1, 0), SatelliteBlocks.BuildAlphaLut(.4f, .4f) }) {
                var block = Block(230, 20, 0xf800, 0x07e0);
                var expected = DecodeMappedEndpoints(block, colors, alpha);
                SatelliteBlocks.Apply(block, colors, alpha);
                Canonical(block);
                Require(expected.SequenceEqual(Decode(block)), "Endpoint remapping changed the decoded palette.");
                // Equal endpoints must use index zero: 3-color index 3 means black,
                // while 6-value alpha indices 6/7 mean 0/255 regardless of endpoints.
                Require(Decode(block, true, true).SequenceEqual(Decode(block)), "Decoder modes disagree.");
            }
        });
        test("satellite Apply handles multiple blocks and rejects partial blocks", () => {
            var colors = SatelliteBlocks.BuildColorLut(Adjustment(brightness: .5f));
            var alpha = SatelliteBlocks.BuildAlphaLut(.8f, .2f);
            var first = Block(255, 0, 0xffff, 0);
            var second = Block(90, 10, 0x3456, 0x1234);
            var together = first.Concat(second).ToArray();
            SatelliteBlocks.Apply(first, colors, alpha); SatelliteBlocks.Apply(second, colors, alpha);
            SatelliteBlocks.Apply(together, colors, alpha);
            Require(together.SequenceEqual(first.Concat(second)), "Not every block was adjusted.");
            SatelliteBlocks.Apply(Span<byte>.Empty, colors, alpha);
            Throws<ArgumentException>(() => SatelliteBlocks.Apply(new byte[17], colors, alpha));
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
            ("magic", 0, 0), ("version", 12, 2), ("format", 16, 2),
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
    }

    private static ColorAdjustment Adjustment(float brightness = 1, float contrast = 1, float saturation = 1) =>
        new(brightness, contrast, saturation, 1, 1, 1, 1, 0, 1);

    private readonly record struct Pixel(int R, int G, int B, int A);

    // Independent reference decoder. The optional forced modes model BC3 hardware
    // versus decoders that also interpret BC1/BC4 endpoint ordering.
    private static Pixel[] Decode(byte[] block, bool forceFourColor = false, bool forceEightAlpha = false)
    {
        var colors = new (int R, int G, int B)[4];
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(8));
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(10));
        colors[0] = Color(c0); colors[1] = Color(c1);
        bool four = forceFourColor || c0 > c1;
        for (int channel = 0; channel < 3; channel++) {
            int x = Channel(colors[0], channel), y = Channel(colors[1], channel);
            SetChannel(ref colors[2], channel, four ? (2 * x + y) / 3 : (x + y) / 2);
            SetChannel(ref colors[3], channel, four ? (x + 2 * y) / 3 : 0);
        }
        var alphas = new int[8]; alphas[0] = block[0]; alphas[1] = block[1];
        bool eight = forceEightAlpha || block[0] > block[1];
        int denominator = eight ? 7 : 5;
        for (int index = 2; index < (eight ? 8 : 6); index++)
            alphas[index] = ((denominator + 1 - index) * block[0] + (index - 1) * block[1]) / denominator;
        if (!eight) { alphas[6] = 0; alphas[7] = 255; }
        ulong alphaIndices = 0;
        for (int i = 0; i < 6; i++) alphaIndices |= (ulong)block[2 + i] << (i * 8);
        uint colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(12));
        return Enumerable.Range(0, 16).Select(p => {
            var c = colors[(colorIndices >> (2 * p)) & 3];
            return new Pixel(c.R, c.G, c.B, alphas[(alphaIndices >> (3 * p)) & 7]);
        }).ToArray();
    }

    private static (int R, int G, int B) Color(ushort c)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
    }
    private static int Channel((int R, int G, int B) c, int i) => i == 0 ? c.R : i == 1 ? c.G : c.B;
    private static void SetChannel(ref (int R, int G, int B) c, int i, int value)
    {
        if (i == 0) c.R = value; else if (i == 1) c.G = value; else c.B = value;
    }
    private static Pixel[] DecodeMappedEndpoints(byte[] original, ushort[] colors, byte[] alpha)
    {
        // Force original 4/8 interpolation even when transformed endpoints reverse
        // or collapse. This is the desired result before canonicalization.
        var mapped = original.ToArray(); mapped[0] = alpha[mapped[0]]; mapped[1] = alpha[mapped[1]];
        BinaryPrimitives.WriteUInt16LittleEndian(mapped.AsSpan(8), colors[BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(8))]);
        BinaryPrimitives.WriteUInt16LittleEndian(mapped.AsSpan(10), colors[BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(10))]);
        return Decode(mapped, true, true);
    }
    private static byte[] Block(byte a0, byte a1, ushort c0, ushort c1)
    {
        var block = new byte[16]; block[0] = a0; block[1] = a1;
        ulong alphaIndices = 0;
        for (int p = 0; p < 16; p++) alphaIndices |= (ulong)(p % 8) << (p * 3);
        for (int i = 0; i < 6; i++) block[2 + i] = (byte)(alphaIndices >> (i * 8));
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), c0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(10), c1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), 0xe4e4e4e4);
        return block;
    }
    private static byte[] RandomBlock(Random random, int ordinal)
    {
        byte a0 = (byte)random.Next(1, 256), a1 = (byte)random.Next(a0);
        ushort c0 = (ushort)random.Next(1, 65536), c1 = (ushort)random.Next(c0);
        var block = Block(a0, a1, c0, c1);
        random.NextBytes(block.AsSpan(2, 6)); random.NextBytes(block.AsSpan(12, 4));
        if (ordinal % 13 == 0) { block[1] = block[0]; Array.Clear(block, 2, 6); }
        if (ordinal % 17 == 0) { block[10] = block[8]; block[11] = block[9]; Array.Clear(block, 12, 4); }
        return block;
    }
    private static void Canonical(byte[] block)
    {
        Require(block[0] >= block[1], "Alpha endpoints reversed.");
        if (block[0] == block[1]) Require(block.AsSpan(2, 6).IndexOfAnyExcept((byte)0) < 0, "Constant alpha has nonzero indices.");
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(8)), c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(10));
        Require(c0 >= c1, "Color endpoints reversed.");
        if (c0 == c1) Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(12)));
        Require(Decode(block, true, true).SequenceEqual(Decode(block)), "Canonical block depends on decoder mode.");
    }
    private static byte[] FileBytes()
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
        writer.Write(new byte[128 - stream.Position]); writer.Write(new byte[80]);
        return stream.ToArray();
    }
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, found {actual}.");
    private static void Near(double expected, double actual, double tolerance, string label) =>
        Require(Math.Abs(expected - actual) <= tolerance, $"{label}: expected {expected:F3} +/- {tolerance}, found {actual}.");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
