using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NuclearSatelliteMap
{
    // Reads and adjusts one file's BC3 payload on a worker thread into unmanaged memory.
    // The main thread uploads it and then calls Free; abandoned jobs are freed once Done.
    internal sealed class SatelliteLoadJob
    {
        internal SatelliteMapFile File { get; }
        internal ColorAdjustment Adjustment { get; }
        internal IntPtr Buffer { get; private set; }
        internal Exception? Error { get; private set; }
        internal double Milliseconds { get; private set; }
        internal bool Adjusted { get; private set; }
        private readonly object gate = new object();
        private bool done, abandoned;
        internal bool Done { get { lock (gate) return done; } }

        private SatelliteLoadJob(SatelliteMapFile file, ColorAdjustment adjustment)
        {
            File = file;
            Adjustment = adjustment;
        }

        internal static SatelliteLoadJob Start(SatelliteMapFile file, ColorAdjustment adjustment)
        {
            var job = new SatelliteLoadJob(file, adjustment);
            Task.Run(job.Run);
            return job;
        }

        private unsafe void Run()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                if (File.DataLength > int.MaxValue) throw new InvalidDataException("Satellite map data exceeds 2 GiB.");
                int length = (int)File.DataLength;
                Buffer = Marshal.AllocHGlobal(length);
                var data = new Span<byte>((void*)Buffer, length);
                using (var stream = new FileStream(File.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                {
                    stream.Position = File.HeaderSize;
                    int offset = 0;
                    while (offset < length)
                    {
                        int read = stream.Read(data.Slice(offset));
                        if (read <= 0) throw new EndOfStreamException($"Satellite map data ended after {offset} of {length} bytes.");
                        offset += read;
                    }
                }
                var colorLut = SatelliteBlocks.BuildColorLut(Adjustment);
                var alphaLut = SatelliteBlocks.BuildAlphaLut(Adjustment.WaterOpacity, Adjustment.LandOpacity);
                if (!SatelliteBlocks.IsIdentity(colorLut, alphaLut))
                {
                    SatelliteBlocks.Apply(data, colorLut, alphaLut);
                    Adjusted = true;
                }
            }
            catch (Exception error)
            {
                Error = error;
                Free();
            }
            Milliseconds = watch.Elapsed.TotalMilliseconds;
            lock (gate)
            {
                done = true;
                if (abandoned) Free();
            }
        }

        // Frees now if finished, otherwise when the worker finishes. Works after the plugin is gone.
        internal void Abandon()
        {
            lock (gate)
            {
                abandoned = true;
                if (done) Free();
            }
        }

        internal void Free()
        {
            if (Buffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Buffer);
            Buffer = IntPtr.Zero;
        }
    }
}
