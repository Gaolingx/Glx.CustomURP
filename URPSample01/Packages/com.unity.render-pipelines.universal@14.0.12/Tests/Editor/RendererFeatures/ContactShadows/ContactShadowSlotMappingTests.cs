using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Record = UnityEngine.Rendering.Universal.ScreenSpaceContactShadowLights.LightRecord;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class ContactShadowSlotMappingTests
    {
        const int SlotCount = 24;
        static int AdditionalLimit => UniversalRenderPipeline.maxVisibleAdditionalLights;
        static readonly FieldInfo s_InstanceId = typeof(VisibleLight).GetField("m_InstanceId",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        readonly List<GameObject> m_Objects = new List<GameObject>();
        ScreenSpaceContactShadowLights m_Builder;

        [SetUp]
        public void SetUp() => m_Builder = new ScreenSpaceContactShadowLights();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in m_Objects)
                UnityEngine.Object.DestroyImmediate(go);
            m_Objects.Clear();
        }

        VisibleLight MakeLight(bool marked, LightType type = LightType.Point)
        {
            var go = new GameObject("Slot mapping test light") { hideFlags = HideFlags.HideAndDontSave };
            m_Objects.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(4, -3, m_Objects.Count), Quaternion.Euler(21, 62, -17));
            var light = go.AddComponent<Light>();
            light.type = type;
            if (marked)
                go.AddComponent<ContactShadowLight>().castContactShadows = true;

            // VisibleLight.light has no setter. Mutate the boxed struct, then unbox the same copy.
            object boxed = new VisibleLight { lightType = type, localToWorldMatrix = go.transform.localToWorldMatrix };
            Assert.That(s_InstanceId, Is.Not.Null, "Unity's VisibleLight instance-ID field changed");
            s_InstanceId.SetValue(boxed, light.GetInstanceID());
            var visible = (VisibleLight)boxed;
            Assert.That(visible.light, Is.EqualTo(light));
            return visible;
        }

        void Build(int mainIndex, params VisibleLight[] lights)
        {
            using (var visible = new NativeArray<VisibleLight>(lights, Allocator.Temp))
            {
                var data = new LightData
                {
                    visibleLights = visible,
                    mainLightIndex = mainIndex,
                    additionalLightsCount = Math.Min(AdditionalLimit, lights.Length - (mainIndex >= 0 ? 1 : 0))
                };
                m_Builder.Build(in data);
            }
            Assert.That(m_Builder.additionalMasks.Length, Is.EqualTo(AdditionalLimit));
            Assert.That(m_Builder.count, Is.InRange(0, SlotCount));
            Assert.That(m_Builder.records.Length, Is.GreaterThanOrEqualTo(m_Builder.count));
        }

        uint Additional(int index) => math.asuint(m_Builder.additionalMasks[index]);

        // 'participants' includes a marked main. At zero, a present main is necessarily unmarked.
        [Test, Combinatorial]
        public void ParticipantBoundaries_AssignContiguousLowBits([Values(0, 1, 23, 24, 25)] int participants,
            [Values(false, true)] bool hasMain)
        {
            int markedMain = hasMain && participants > 0 ? 1 : 0;
            int additionalCount = participants - markedMain;
            var lights = new VisibleLight[additionalCount + (hasMain ? 1 : 0)];
            for (int i = 0; i < lights.Length; ++i)
                lights[i] = hasMain && i == 0 ? MakeLight(markedMain == 1, LightType.Directional) : MakeLight(true);
            Build(hasMain ? 0 : -1, lights);

            int expected = Math.Min(SlotCount, markedMain + Math.Min(AdditionalLimit, additionalCount));
            Assert.That(m_Builder.count, Is.EqualTo(expected));
            Assert.That(m_Builder.mainMask, Is.EqualTo((uint)markedMain));
            for (int i = 0; i < AdditionalLimit; ++i)
                Assert.That(Additional(i), Is.EqualTo(i + markedMain < expected ? 1u << (i + markedMain) : 0u), $"additional[{i}]");
            for (int i = 0; i < expected; ++i)
                Assert.That(m_Builder.records[i].mask, Is.EqualTo(1u << i), $"record[{i}]");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void NonFirstMain_SkipsOnlyMainAndPreservesAdditionalHoles(bool markMain)
        {
            var point = MakeLight(true);
            var spot = MakeLight(true, LightType.Spot);
            Build(2, MakeLight(false), point, MakeLight(markMain, LightType.Directional), MakeLight(false), spot);
            int first = markMain ? 1 : 0;
            Assert.That(m_Builder.mainMask, Is.EqualTo((uint)first));
            Assert.That(m_Builder.count, Is.EqualTo(first + 2));
            Assert.That(new[] { Additional(0), Additional(1), Additional(2), Additional(3) },
                Is.EqualTo(new[] { 0u, 1u << first, 0u, 2u << first }));
            AssertRecord(first, point, 1u);
            AssertRecord(first + 1, spot, 2u);
        }

        void AssertRecord(int slot, VisibleLight visible, uint encodedType)
        {
            var record = m_Builder.records[slot];
            var transform = visible.light.transform;
            Assert.That(record.mask, Is.EqualTo(1u << slot));
            Assert.That(record.type, Is.EqualTo(encodedType));
            Assert.That(Vector3.Distance(record.position, encodedType == 0 ? Vector3.zero : transform.position), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(record.direction, encodedType == 0 ? -transform.forward : Vector3.zero), Is.LessThan(1e-5f));
        }

        [Test]
        public void Records_MatchShaderLayoutTypesAndWorldVectors()
        {
            Assert.That(Marshal.SizeOf(typeof(Record)), Is.EqualTo(32));
            Assert.That(Marshal.OffsetOf(typeof(Record), nameof(Record.position)).ToInt32(), Is.Zero);
            Assert.That(Marshal.OffsetOf(typeof(Record), nameof(Record.type)).ToInt32(), Is.EqualTo(12));
            Assert.That(Marshal.OffsetOf(typeof(Record), nameof(Record.direction)).ToInt32(), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf(typeof(Record), nameof(Record.mask)).ToInt32(), Is.EqualTo(28));
            var main = MakeLight(true, LightType.Directional);
            var sun = MakeLight(true, LightType.Directional);
            var point = MakeLight(true);
            var spot = MakeLight(true, LightType.Spot);
            Build(1, point, main, spot, sun);
            Assert.That(m_Builder.count, Is.EqualTo(4));
            AssertRecord(0, main, 0);
            AssertRecord(1, point, 1);
            AssertRecord(2, spot, 2);
            AssertRecord(3, sun, 0);
        }

        [Test]
        public void Rebuild_ClearsMainAndEntireAdditionalArray()
        {
            var lights = new VisibleLight[1 + AdditionalLimit];
            lights[0] = MakeLight(true, LightType.Directional);
            lights[lights.Length - 1] = MakeLight(true);
            Build(0, lights);
            Assert.That(m_Builder.mainMask, Is.EqualTo(1u));
            Assert.That(Additional(AdditionalLimit - 1), Is.EqualTo(2u));
            Build(-1, MakeLight(true));
            Assert.That(m_Builder.count, Is.EqualTo(1));
            Assert.That(m_Builder.mainMask, Is.Zero);
            Assert.That(Additional(0), Is.EqualTo(1u));
            for (int i = 1; i < AdditionalLimit; ++i)
                Assert.That(Additional(i), Is.Zero, $"additional[{i}]");
            Build(-1);
            Assert.That(m_Builder.count, Is.Zero);
            Assert.That(Additional(0), Is.Zero);
        }

        [Test]
        public void Rebuild_DisabledMarkersAndRectangleKeepTheirAdditionalIndices()
        {
            var point = MakeLight(true);
            var spot = MakeLight(true, LightType.Spot);
            var area = MakeLight(true, LightType.Rectangle);
            var survivor = MakeLight(true);
            Build(-1, point, spot, area, survivor);
            Assert.That(m_Builder.count, Is.EqualTo(3));
            Assert.That(new[] { Additional(0), Additional(1), Additional(2), Additional(3) },
                Is.EqualTo(new[] { 1u, 2u, 0u, 4u }));

            point.light.GetComponent<ContactShadowLight>().castContactShadows = false;
            spot.light.GetComponent<ContactShadowLight>().enabled = false;
            Build(-1, point, spot, area, survivor);
            Assert.That(m_Builder.count, Is.EqualTo(1));
            Assert.That(new[] { Additional(0), Additional(1), Additional(2), Additional(3) },
                Is.EqualTo(new[] { 0u, 0u, 0u, 1u }));
            AssertRecord(0, survivor, 1);
        }

        [Test]
        public void PlatformLimit_TruncatesAdditionalIndicesNotVisibleIndices()
        {
            // Null-light holes consume indices cheaply. Leave slots free so the 24-bit cap cannot hide overruns.
            var lights = new VisibleLight[AdditionalLimit + 2];
            lights[1] = MakeLight(true, LightType.Directional);
            lights[AdditionalLimit] = MakeLight(true);
            lights[AdditionalLimit + 1] = MakeLight(true, LightType.Spot);
            Build(1, lights);
            Assert.That(m_Builder.count, Is.EqualTo(2));
            Assert.That(Additional(AdditionalLimit - 1), Is.EqualTo(2u));
            AssertRecord(1, lights[AdditionalLimit], 1);
        }

        [Test]
        public void FloatMaskUpload_RoundTripsThroughShaderAsUint()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("Requires compute shader readback.");

            var lights = new VisibleLight[Math.Min(AdditionalLimit, SlotCount + 2)];
            for (int i = 1; i < lights.Length; ++i)
                lights[i] = MakeLight(true);
            Build(-1, lights);

            const string masks = "_ContactShadowTestMaskBits";
            string path = "Assets/ContactShadowBits_" + Guid.NewGuid().ToString("N") + ".compute";
            try
            {
                File.WriteAllText(path, "#pragma kernel Probe\n" +
                    $"float {masks}[{AdditionalLimit}]; RWStructuredBuffer<uint> _Words;\n" +
                    $"[numthreads(1,1,1)] void Probe(uint3 id : SV_DispatchThreadID) {{ _Words[id.x] = asuint({masks}[id.x]); }}\n");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var probe = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                Assert.That(probe, Is.Not.Null);
                int kernel = probe.FindKernel("Probe");
                using var words = new ComputeBuffer(AdditionalLimit, sizeof(uint));
                using var cmd = new CommandBuffer();
                cmd.SetGlobalFloatArray(masks, m_Builder.additionalMasks);
                cmd.SetComputeBufferParam(probe, kernel, "_Words", words);
                cmd.DispatchCompute(probe, kernel, AdditionalLimit, 1, 1);
                Graphics.ExecuteCommandBuffer(cmd);
                var bits = new uint[AdditionalLimit];
                words.GetData(bits);
                for (int i = 0; i < bits.Length; ++i)
                    Assert.That(bits[i], Is.EqualTo(i > 0 && i <= SlotCount ? 1u << (i - 1) : 0u), $"GPU additional[{i}]");
            }
            finally
            {
                Shader.SetGlobalFloatArray(masks, new float[AdditionalLimit]);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void MixedMarkedAndOverflow_ProducesSlotDebugImage()
        {
            // Visible order: hole, main, 23 accepted points, one overflow point.
            // The full fixture requires 25 additional indices; do not inject a desktop cap on mobile.
            if (AdditionalLimit < 25)
                Assert.Ignore("This evidence fixture requires 25 visible additional indices.");
            var lights = new VisibleLight[26];
            lights[0] = MakeLight(false);
            lights[1] = MakeLight(true, LightType.Directional);
            for (int i = 2; i < lights.Length; ++i)
                lights[i] = MakeLight(true);
            Build(1, lights);

            // Rows top-down follow visible order. Left stripe: gray=unmarked, blue=main, orange=marked.
            // Remaining columns are measured mask bits 0..23 left-to-right (white=set, black=clear).
            const int cell = 8;
            var image = new Texture2D((1 + SlotCount) * cell, lights.Length * cell, TextureFormat.RGB24, false);
            try
            {
                for (int row = 0; row < lights.Length; ++row)
                {
                    uint mask = row == 1 ? m_Builder.mainMask : Additional(row == 0 ? 0 : row - 1);
                    var stripe = row == 0 ? Color.gray : row == 1 ? Color.blue : new Color(1f, 0.5f, 0f);
                    for (int x = 0; x < image.width; ++x)
                    {
                        int col = x / cell;
                        var color = col == 0 ? stripe : (mask & (1u << (col - 1))) != 0 ? Color.white : Color.black;
                        for (int y = 0; y < cell; ++y)
                            image.SetPixel(x, (lights.Length - 1 - row) * cell + y, color);
                    }
                    Assert.That(mask, Is.EqualTo(row > 0 && row <= SlotCount ? 1u << (row - 1) : 0u), $"visible[{row}]");
                }
                image.Apply();
                var png = image.EncodeToPNG();
                Assert.That(png, Is.Not.Empty);
                var args = Environment.GetCommandLineArgs();
                int evidence = Array.IndexOf(args, "-contactShadowT4Evidence");
                if (evidence >= 0)
                {
                    File.WriteAllBytes(Path.Combine(args[evidence + 1], "SlotMapping.png"), png);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }
}
