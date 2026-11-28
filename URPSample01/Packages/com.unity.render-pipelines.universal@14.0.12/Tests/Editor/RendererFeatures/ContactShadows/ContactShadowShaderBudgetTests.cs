using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace UnityEditor.Rendering.Universal.Tests
{
    [TestFixture]
    class ContactShadowShaderBudgetTests
    {
        static readonly string[] FrozenAxes = {
            "_ _GBUFFER_NORMALS_OCT",
            "_ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN",
            "_ _ADDITIONAL_LIGHT_SHADOWS",
            "_ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH",
            "_ _LIGHT_COOKIES", "_ _LIGHT_LAYERS", "_ _SCREEN_SPACE_OCCLUSION", "_ _DEFERRED_MIXED_LIGHTING"
        };

        static string[] Declarations() => File.ReadLines(Path.GetFullPath(ClusteredDeferredAssets.ComputeAssetPath))
            .Select(line => line.Trim()).Where(line => line.StartsWith("#pragma multi_compile", StringComparison.Ordinal))
            .Select(line => string.Join(" ", line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();

        // _CONTACT_SHADOWS_DEFERRED and _GLX_SSR_SPECULAR are the two axes added after the frozen
        // eight; neither may reorder or replace them.
        [Test]
        public void OnlyKnownAxesFollowFrozenDeclarations()
        {
            CollectionAssert.AreEqual(
                FrozenAxes.Concat(new[] { "_ _CONTACT_SHADOWS_DEFERRED", "_ _GLX_SSR_SPECULAR" })
                    .Select(axis => "#pragma multi_compile " + axis).ToArray(), Declarations());
        }

        [Test]
        public void ContactAndSsrAxesGrowTheFrozenBudget()
        {
            var widths = Declarations().Select(line => line.Split(' ').Length - 2).ToArray();
            CollectionAssert.AreEqual(new[] { 2, 4, 2, 5, 2, 2, 2, 2 }, widths.Take(8).ToArray());
            CollectionAssert.AreEqual(new[] { 2, 2 }, widths.Skip(8).ToArray());
            Assert.AreEqual(1280, widths.Take(8).Aggregate(1, (n, width) => n * width));
            Assert.AreEqual(2560, widths.Take(9).Aggregate(1, (n, width) => n * width));
            Assert.AreEqual(5120, widths.Aggregate(1, (n, width) => n * width));
        }
    }
}
