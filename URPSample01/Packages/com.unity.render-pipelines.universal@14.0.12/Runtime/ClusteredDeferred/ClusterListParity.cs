using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;

namespace UnityEngine.Rendering.Universal.Internal
{
    // CPU-side capture of the two cluster bitmaps plus the parameters the CPU published for them.
    // G1 compares this word-for-word with the GPU builder's readback: no float tolerance, a mismatch
    // is reported with its word index instead of averaged away. Only the used prefix (what the kernel
    // can index) counts towards Exact; the tail is tracked separately because a GPU clear that stops
    // at the used region is legitimate.
    internal struct ClusterListSnapshot
    {
        public const int FormatVersion = 1;
        const uint k_Magic = 0x504C4443; // "CDLP"

        public uint[] zBins;
        public uint[] tileMasks;
        public ClusterItemRecord[] items;
        public float[] fpParams; // _FPParams0/1/2, 12 floats in declaration order

        public float zBinScale;
        public float zBinOffset;
        public int wordsPerTile;
        public int binCount;
        public int lightCount;
        public int directionalLightCount;
        public int viewCount;
        public int tileWidth;
        public Vector2Int tileResolution;
        public Vector2Int screenResolution;

        public int ZBinUsedWords => binCount * (ZBinningJob.headerLength + wordsPerTile) * viewCount;

        public int TileUsedWords => tileResolution.x * tileResolution.y * wordsPerTile * viewCount;

        public void CopyBitmapsFrom(NativeArray<uint> zBins, NativeArray<uint> tileMasks)
        {
            this.zBins = new uint[zBins.Length];
            this.tileMasks = new uint[tileMasks.Length];
            zBins.CopyTo(this.zBins);
            tileMasks.CopyTo(this.tileMasks);
        }

        public void WriteFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(k_Magic);
            writer.Write(FormatVersion);
            writer.Write(zBinScale);
            writer.Write(zBinOffset);
            writer.Write(wordsPerTile);
            writer.Write(binCount);
            writer.Write(lightCount);
            writer.Write(directionalLightCount);
            writer.Write(viewCount);
            writer.Write(tileWidth);
            writer.Write(tileResolution.x);
            writer.Write(tileResolution.y);
            writer.Write(screenResolution.x);
            writer.Write(screenResolution.y);

            WriteUInts(writer, zBins);
            WriteUInts(writer, tileMasks);

            var parameters = fpParams ?? Array.Empty<float>();
            writer.Write(parameters.Length);
            foreach (var value in parameters)
                writer.Write(value);

            var items = this.items ?? Array.Empty<ClusterItemRecord>();
            writer.Write(items.Length);
            foreach (var item in items)
            {
                writer.Write(item.positionRange.x);
                writer.Write(item.positionRange.y);
                writer.Write(item.positionRange.z);
                writer.Write(item.positionRange.w);
                writer.Write(item.directionExtent.x);
                writer.Write(item.directionExtent.y);
                writer.Write(item.directionExtent.z);
                writer.Write(item.directionExtent.w);
                writer.Write((uint)item.itemType);
                writer.Write((int)item.lightType);
                writer.Write(item.layerMask);
                writer.Write(item.sourceIndex);
            }
        }

        public static ClusterListSnapshot ReadFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != k_Magic)
                throw new InvalidDataException("Not a cluster list snapshot: " + path);
            var version = reader.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException($"Snapshot format {version} is not {FormatVersion}: {path}");

            var snapshot = new ClusterListSnapshot
            {
                zBinScale = reader.ReadSingle(),
                zBinOffset = reader.ReadSingle(),
                wordsPerTile = reader.ReadInt32(),
                binCount = reader.ReadInt32(),
                lightCount = reader.ReadInt32(),
                directionalLightCount = reader.ReadInt32(),
                viewCount = reader.ReadInt32(),
                tileWidth = reader.ReadInt32(),
            };
            snapshot.tileResolution = new Vector2Int(reader.ReadInt32(), reader.ReadInt32());
            snapshot.screenResolution = new Vector2Int(reader.ReadInt32(), reader.ReadInt32());

            snapshot.zBins = ReadUInts(reader);
            snapshot.tileMasks = ReadUInts(reader);

            var parameters = new float[reader.ReadInt32()];
            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = reader.ReadSingle();
            snapshot.fpParams = parameters;

            var items = new ClusterItemRecord[reader.ReadInt32()];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new ClusterItemRecord
                {
                    positionRange = new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    directionExtent = new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    itemType = (ClusterItemType)reader.ReadUInt32(),
                    lightType = (LightType)reader.ReadInt32(),
                    layerMask = reader.ReadUInt32(),
                    sourceIndex = reader.ReadUInt32(),
                };
            }
            snapshot.items = items;
            return snapshot;
        }

        // Compares the published parameters first (a differing parameter makes the bitmap comparison
        // meaningless), then the two used prefixes word by word. The tail is counted but does not
        // affect Exact.
        public static ClusterListDiff Diff(in ClusterListSnapshot expected, in ClusterListSnapshot actual)
        {
            var diff = new ClusterListDiff
            {
                parameterMismatches = new List<string>(),
                zBinExamples = new List<int>(),
                tileMaskExamples = new List<int>(),
            };

            CompareFloat("zBinScale", expected.zBinScale, actual.zBinScale, diff.parameterMismatches);
            CompareFloat("zBinOffset", expected.zBinOffset, actual.zBinOffset, diff.parameterMismatches);
            CompareInt("wordsPerTile", expected.wordsPerTile, actual.wordsPerTile, diff.parameterMismatches);
            CompareInt("binCount", expected.binCount, actual.binCount, diff.parameterMismatches);
            CompareInt("lightCount", expected.lightCount, actual.lightCount, diff.parameterMismatches);
            CompareInt("directionalLightCount", expected.directionalLightCount, actual.directionalLightCount, diff.parameterMismatches);
            CompareInt("viewCount", expected.viewCount, actual.viewCount, diff.parameterMismatches);
            CompareInt("tileWidth", expected.tileWidth, actual.tileWidth, diff.parameterMismatches);
            CompareInt("tileResolution.x", expected.tileResolution.x, actual.tileResolution.x, diff.parameterMismatches);
            CompareInt("tileResolution.y", expected.tileResolution.y, actual.tileResolution.y, diff.parameterMismatches);
            CompareInt("screenResolution.x", expected.screenResolution.x, actual.screenResolution.x, diff.parameterMismatches);
            CompareInt("screenResolution.y", expected.screenResolution.y, actual.screenResolution.y, diff.parameterMismatches);
            CompareInt("zBins.Length", expected.zBins?.Length ?? 0, actual.zBins?.Length ?? 0, diff.parameterMismatches);
            CompareInt("tileMasks.Length", expected.tileMasks?.Length ?? 0, actual.tileMasks?.Length ?? 0, diff.parameterMismatches);
            CompareInt("items.Length", expected.items?.Length ?? 0, actual.items?.Length ?? 0, diff.parameterMismatches);
            CompareInt("fpParams.Length", expected.fpParams?.Length ?? 0, actual.fpParams?.Length ?? 0, diff.parameterMismatches);

            var expectedParams = expected.fpParams ?? Array.Empty<float>();
            var actualParams = actual.fpParams ?? Array.Empty<float>();
            for (int i = 0; i < math.min(expectedParams.Length, actualParams.Length); i++)
                CompareFloat("_FPParams[" + i + "]", expectedParams[i], actualParams[i], diff.parameterMismatches);

            CompareWords(expected.zBins, actual.zBins, expected.ZBinUsedWords,
                ref diff.zBinMismatchWords, ref diff.zBinMismatchBits, ref diff.zBinOutsideUsed,
                ref diff.zBinMissingBits, ref diff.zBinExtraBits, diff.zBinExamples);
            CompareWords(expected.tileMasks, actual.tileMasks, expected.TileUsedWords,
                ref diff.tileMaskMismatchWords, ref diff.tileMaskMismatchBits, ref diff.tileMaskOutsideUsed,
                ref diff.tileMaskMissingBits, ref diff.tileMaskExtraBits, diff.tileMaskExamples);

            return diff;
        }

        static void CompareWords(uint[] expected, uint[] actual, int usedWords,
            ref int mismatchWords, ref int mismatchBits, ref int outsideUsed,
            ref int missingBits, ref int extraBits, List<int> examples)
        {
            if (expected == null || actual == null)
                return;

            var length = math.min(expected.Length, actual.Length);
            for (int i = 0; i < length; i++)
            {
                if (expected[i] == actual[i])
                    continue;

                if (i >= usedWords)
                {
                    outsideUsed++;
                    continue;
                }

                mismatchWords++;
                var xor = expected[i] ^ actual[i];
                mismatchBits += math.countbits(xor);
                missingBits += math.countbits(expected[i] & ~actual[i]);
                extraBits += math.countbits(actual[i] & ~expected[i]);
                if (examples.Count < k_MaxExamples)
                    examples.Add(i);
            }
        }

        const int k_MaxExamples = 8;

        static void CompareInt(string name, int expected, int actual, List<string> mismatches)
        {
            if (expected != actual)
                mismatches.Add($"{name}: {expected} != {actual}");
        }

        static void CompareFloat(string name, float expected, float actual, List<string> mismatches)
        {
            // Bitwise, so -0.0 and 0.0 differ too: the CPU publishes one specific bit pattern.
            if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
                mismatches.Add($"{name}: {expected:R} != {actual:R}");
        }

        static void WriteUInts(BinaryWriter writer, uint[] values)
        {
            values ??= Array.Empty<uint>();
            writer.Write(values.Length);
            foreach (var value in values)
                writer.Write(value);
        }

        static uint[] ReadUInts(BinaryReader reader)
        {
            var values = new uint[reader.ReadInt32()];
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadUInt32();
            return values;
        }
    }

    internal struct ClusterListDiff
    {
        public int zBinMismatchWords;
        public int tileMaskMismatchWords;
        public int zBinMismatchBits;
        public int tileMaskMismatchBits;
        public int zBinOutsideUsed;
        public int tileMaskOutsideUsed;
        // Bits the GPU sets but the CPU does not (expected for the conservative tile AABB) and bits
        // the CPU sets that the GPU misses (never acceptable: those lights would stop being shaded).
        public int zBinMissingBits;
        public int zBinExtraBits;
        public int tileMaskMissingBits;
        public int tileMaskExtraBits;
        public List<string> parameterMismatches;
        public List<int> zBinExamples;
        public List<int> tileMaskExamples;

        public bool Exact => zBinMismatchWords == 0 &&
                             tileMaskMismatchWords == 0 &&
                             (parameterMismatches == null || parameterMismatches.Count == 0);

        // The G1 tile gate: every CPU bit must be present, extra bits are allowed and counted.
        public bool CoversCpuBits => zBinMissingBits == 0 &&
                                     tileMaskMissingBits == 0 &&
                                     (parameterMismatches == null || parameterMismatches.Count == 0);

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append("cluster list diff: ");
            builder.Append(Exact ? "exact" : "MISMATCH");
            builder.Append($"; zBins {zBinMismatchWords} words/{zBinMismatchBits} bits");
            builder.Append($"; tileMasks {tileMaskMismatchWords} words/{tileMaskMismatchBits} bits");
            if (zBinMissingBits != 0 || tileMaskMissingBits != 0)
                builder.Append($"; CPU bits missing on GPU: zBins {zBinMissingBits}, tileMasks {tileMaskMissingBits}");
            if (zBinExtraBits != 0 || tileMaskExtraBits != 0)
                builder.Append($"; extra GPU bits: zBins {zBinExtraBits}, tileMasks {tileMaskExtraBits}");
            if (zBinOutsideUsed != 0 || tileMaskOutsideUsed != 0)
                builder.Append($"; outside used region: zBins {zBinOutsideUsed}, tileMasks {tileMaskOutsideUsed}");
            if (zBinExamples != null && zBinExamples.Count != 0)
                builder.Append("; first zBin words " + string.Join(",", zBinExamples));
            if (tileMaskExamples != null && tileMaskExamples.Count != 0)
                builder.Append("; first tile words " + string.Join(",", tileMaskExamples));
            if (parameterMismatches != null && parameterMismatches.Count != 0)
                builder.Append("; params: " + string.Join(" | ", parameterMismatches));
            return builder.ToString();
        }
    }

    // Disabled by default; only the parity tests and the G1 runner turn it on. Every call site pays
    // one bool check when it is off, the same pattern as ClusteredDeferredCpuProbe.
    internal static class ClusterListParityCapture
    {
        public static bool enabled;
        public static ClusterListSnapshot last;
        // Filled asynchronously by the ordered readback, so tests have to wait a frame for it.
        public static ClusterListSnapshot lastGpu;

        public static void Capture(ClusterListSnapshot snapshot) => last = snapshot;

        // CommandBuffer.RequestAsyncReadback is ordered after the dispatch, unlike
        // AsyncGPUReadback.Request, which would read the previous frame's buffer contents.
        public static void RequestGpuCapture(CommandBuffer cmd, ClusterListSnapshot metadata, GraphicsBuffer zbins, GraphicsBuffer tiles)
        {
            if (cmd == null || zbins == null || tiles == null)
                return;

            var pending = new PendingCapture { snapshot = metadata, remaining = 2 };
            // Read the sizes now: the buffers can be disposed before the callback fires.
            int zBinCount = zbins.count;
            int tileCount = tiles.count;
            cmd.RequestAsyncReadback(zbins, request => OnReadback(request, pending, zBinCount, true));
            cmd.RequestAsyncReadback(tiles, request => OnReadback(request, pending, tileCount, false));
        }

        sealed class PendingCapture
        {
            public ClusterListSnapshot snapshot;
            public int remaining;
        }

        static void OnReadback(AsyncGPUReadbackRequest request, PendingCapture pending, int wordCount, bool isZBin)
        {
            if (pending.remaining <= 0)
                return;

            if (request.hasError)
            {
                pending.remaining = 0;
                Debug.LogWarning("Cluster list GPU parity readback failed.");
                return;
            }

            var words = new uint[wordCount];
            request.GetData<uint>().CopyTo(words);
            if (isZBin)
                pending.snapshot.zBins = words;
            else
                pending.snapshot.tileMasks = words;

            if (--pending.remaining == 0)
                lastGpu = pending.snapshot;
        }

        public static void Reset()
        {
            enabled = false;
            last = default;
            lastGpu = default;
        }
    }
}
