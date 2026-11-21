using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

namespace UnityEditor.Rendering.Universal.Tests
{
    // G1 parity harness, CPU half. The GPU builder has to reproduce these two bitmaps word for word,
    // so the layout and the comparison rules are pinned here before any compute is written.
    [TestFixture]
    class ClusteredDeferredGpuListParityTests
    {
        // Changing any of these offsets changes the stream the GPU reads; update the HANDOFF table,
        // the HLSL mirror and the parity golden together.
        [Test]
        public void ClusterItemRecordLayoutIsFrozen()
        {
            Assert.AreEqual(ClusterItemRecord.Stride, Marshal.SizeOf<ClusterItemRecord>(),
                "ClusterItemRecord grew or shrank; the GPU struct in the G1 kernel must match.");
            Assert.AreEqual(48, ClusterItemRecord.Stride);

            Assert.AreEqual(0, OffsetOf(nameof(ClusterItemRecord.positionRange)));
            Assert.AreEqual(16, OffsetOf(nameof(ClusterItemRecord.directionExtent)));
            Assert.AreEqual(32, OffsetOf(nameof(ClusterItemRecord.itemType)));
            Assert.AreEqual(36, OffsetOf(nameof(ClusterItemRecord.lightType)));
            Assert.AreEqual(40, OffsetOf(nameof(ClusterItemRecord.layerMask)));
            Assert.AreEqual(44, OffsetOf(nameof(ClusterItemRecord.sourceIndex)));

            Assert.AreEqual(0u, (uint)ClusterItemType.Light);
            Assert.AreEqual(1u, (uint)ClusterItemType.Probe);
        }

        static int OffsetOf(string field) => Marshal.OffsetOf<ClusterItemRecord>(field).ToInt32();

        [Test]
        public void SnapshotRoundTripsThroughDisk()
        {
            var expected = MakeSnapshot();
            var path = TempPath("roundtrip.bin");
            try
            {
                expected.WriteFile(path);
                var actual = ClusterListSnapshot.ReadFile(path);

                var diff = ClusterListSnapshot.Diff(expected, actual);
                Assert.IsTrue(diff.Exact, diff.ToString());
                CollectionAssert.AreEqual(expected.zBins, actual.zBins);
                CollectionAssert.AreEqual(expected.tileMasks, actual.tileMasks);
                CollectionAssert.AreEqual(expected.fpParams, actual.fpParams);
                Assert.AreEqual(expected.items.Length, actual.items.Length);
                for (int i = 0; i < expected.items.Length; i++)
                {
                    Assert.AreEqual(expected.items[i].itemType, actual.items[i].itemType);
                    Assert.AreEqual(expected.items[i].lightType, actual.items[i].lightType);
                    Assert.AreEqual(expected.items[i].layerMask, actual.items[i].layerMask);
                    Assert.AreEqual(expected.items[i].sourceIndex, actual.items[i].sourceIndex);
                    Assert.AreEqual(Bits(expected.items[i].positionRange), Bits(actual.items[i].positionRange));
                    Assert.AreEqual(Bits(expected.items[i].directionExtent), Bits(actual.items[i].directionExtent));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void DiffLocalizesAPlantedBitFlip()
        {
            var expected = MakeSnapshot();
            var actual = MakeSnapshot();
            actual.tileMasks[7] ^= 1u << 3;

            var diff = ClusterListSnapshot.Diff(expected, actual);
            Assert.IsFalse(diff.Exact);
            Assert.AreEqual(0, diff.zBinMismatchWords);
            Assert.AreEqual(1, diff.tileMaskMismatchWords);
            Assert.AreEqual(1, diff.tileMaskMismatchBits, "A single flipped bit must not be reported as a word-level difference.");
            CollectionAssert.AreEqual(new[] { 7 }, diff.tileMaskExamples);
        }

        [Test]
        public void DiffIgnoresTheTailBeyondTheUsedRegion()
        {
            var expected = MakeSnapshot();
            var actual = MakeSnapshot();
            var usedWords = expected.TileUsedWords;
            Assert.Less(usedWords, actual.tileMasks.Length, "The fixture needs a tail to test the used-region boundary.");
            actual.tileMasks[usedWords] = 0xFFFFFFFFu;

            var diff = ClusterListSnapshot.Diff(expected, actual);
            Assert.IsTrue(diff.Exact, diff.ToString());
            Assert.AreEqual(1, diff.tileMaskOutsideUsed,
                "Writes past the used prefix are reported but must not fail parity: the kernel cannot index them.");
        }

        [Test]
        public void DiffRejectsParameterDrift()
        {
            var expected = MakeSnapshot();
            var actual = MakeSnapshot();
            actual.tileWidth = 32;
            actual.fpParams[0] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(actual.fpParams[0]) + 1);

            var diff = ClusterListSnapshot.Diff(expected, actual);
            Assert.IsFalse(diff.Exact);
            Assert.AreEqual(2, diff.parameterMismatches.Count, string.Join(" | ", diff.parameterMismatches));
            Assert.IsTrue(diff.parameterMismatches.Exists(m => m.StartsWith("tileWidth")));
            Assert.IsTrue(diff.parameterMismatches.Exists(m => m.StartsWith("_FPParams[0]")),
                "Float parameters are compared bitwise; one ulp is a mismatch.");
        }

        // 640x360 at 16px tiles -> 40x23 tiles, 2 words per tile, so the used prefix is a small
        // fraction of both buffers and the tail rule is testable.
        static ClusterListSnapshot MakeSnapshot()
        {
            var snapshot = new ClusterListSnapshot
            {
                zBins = new uint[UniversalRenderPipeline.maxZBinWords],
                tileMasks = new uint[UniversalRenderPipeline.maxTileWords],
                items = new[]
                {
                    new ClusterItemRecord
                    {
                        positionRange = new float4(1f, 2f, 3f, 5f),
                        directionExtent = new float4(0f, 0f, 1f, 0f),
                        itemType = ClusterItemType.Light,
                        lightType = LightType.Point,
                        layerMask = 1u,
                        sourceIndex = 1u,
                    },
                    new ClusterItemRecord
                    {
                        positionRange = new float4(-1f, 0.5f, 2f, 7f),
                        directionExtent = new float4(0f, 0f, -1f, 45f),
                        itemType = ClusterItemType.Light,
                        lightType = LightType.Spot,
                        layerMask = 0x80000000u,
                        sourceIndex = 2u,
                    },
                },
                fpParams = new[] { 1.5f, -0.5f, 2f, 0f, 40f, 22.5f, 2f, 0f, 12f, 920f, 0f, 0f },
                zBinScale = 1.5f,
                zBinOffset = -0.5f,
                wordsPerTile = 2,
                binCount = 8,
                lightCount = 2,
                directionalLightCount = 0,
                viewCount = 1,
                tileWidth = 16,
                tileResolution = new Vector2Int(40, 23),
                screenResolution = new Vector2Int(640, 360),
            };

            snapshot.zBins[0] = 0x00010001u;
            snapshot.zBins[1] = 0x00000003u;
            snapshot.tileMasks[5] = 0xDEADBEEFu;
            return snapshot;
        }

        static string Bits(float4 value) =>
            string.Join(",", BitConverter.SingleToInt32Bits(value.x), BitConverter.SingleToInt32Bits(value.y),
                BitConverter.SingleToInt32Bits(value.z), BitConverter.SingleToInt32Bits(value.w));

        static string TempPath(string fileName)
        {
            var directory = Path.Combine(Application.temporaryCachePath, "ClusteredDeferredParity");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, fileName);
        }
    }
}
