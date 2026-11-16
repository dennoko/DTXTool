using Dennoko.DTXTool.Core;
using NUnit.Framework;
using UnityEngine;

namespace Dennoko.DTXTool.Tests
{
    /// <summary>合成の式の CPU 実装（シェーダーと突き合わせる側の基準）。</summary>
    public sealed class CoreMathTests
    {
        private const float Eps = 1e-5f;

        [TestCase(BlendMode.Normal, 0.25f, 0.75f, 0.75f)]
        [TestCase(BlendMode.Multiply, 0.5f, 0.5f, 0.25f)]
        [TestCase(BlendMode.Additive, 0.25f, 0.5f, 0.75f)]
        [TestCase(BlendMode.Additive, 0.75f, 0.5f, 1f)]
        [TestCase(BlendMode.Screen, 0.5f, 0.5f, 0.75f)]
        [TestCase(BlendMode.Overlay, 0.25f, 0.5f, 0.25f)]
        [TestCase(BlendMode.Overlay, 0.75f, 0.5f, 0.75f)]
        [TestCase(BlendMode.Overlay, 0.25f, 1f, 0.5f)]
        public void Blend_KnownValues(BlendMode mode, float d, float s, float expected)
        {
            Assert.AreEqual(expected, BlendMath.Blend(mode, d, s), Eps);
        }

        [Test]
        public void Blend_NeutralColorsLeaveBaseUnchanged()
        {
            foreach (float d in new[] { 0f, 0.2f, 0.5f, 0.9f, 1f })
            {
                Assert.AreEqual(d, BlendMath.Blend(BlendMode.Multiply, d, 1f), Eps);
                Assert.AreEqual(d, BlendMath.Blend(BlendMode.Additive, d, 0f), Eps);
                Assert.AreEqual(d, BlendMath.Blend(BlendMode.Screen, d, 0f), Eps);
                Assert.AreEqual(d, BlendMath.Blend(BlendMode.Overlay, d, 0.5f), Eps);
            }
        }

        [Test]
        public void Composite_LerpsByCoverageAndKeepsDestinationAlpha()
        {
            var dst = new Color(0.2f, 0.4f, 0.6f, 0.3f);
            var src = new Color(1f, 0f, 0.5f, 1f);
            var half = BlendMath.Composite(BlendMode.Normal, dst, src, 0.5f);
            Assert.AreEqual(0.6f, half.r, Eps);
            Assert.AreEqual(0.2f, half.g, Eps);
            Assert.AreEqual(0.55f, half.b, Eps);
            Assert.AreEqual(0.3f, half.a, Eps, "alpha comes from the layer below");

            var none = BlendMath.Composite(BlendMode.Multiply, dst, src, 0f);
            Assert.AreEqual(dst.r, none.r, Eps);
            var over = BlendMath.Composite(BlendMode.Normal, dst, src, 5f);
            Assert.AreEqual(1f, over.r, Eps, "coverage is clamped");
        }

        [Test]
        public void Coverage_AndFinishMask()
        {
            Assert.AreEqual(0.125f, BlendMath.Coverage(0.5f, 0.5f, 0.5f), Eps);
            Assert.AreEqual(0f, BlendMath.Coverage(-1f, 1f, 1f), Eps);
            Assert.AreEqual(0.25f, BlendMath.FinishMask(0.5f, false, 0.5f), Eps);
            Assert.AreEqual(0.75f, BlendMath.FinishMask(0.25f, true, 1f), Eps);
        }

        [Test]
        public void ColorSpace_RoundTripsAndMatchesKnownPoints()
        {
            Assert.AreEqual(0f, BlendMath.LinearToGamma(0f), Eps);
            Assert.AreEqual(1f, BlendMath.LinearToGamma(1f), Eps);
            Assert.AreEqual(0.5f, BlendMath.LinearToGamma(0.21404114f), 1e-4f);
            Assert.AreEqual(0.21404114f, BlendMath.GammaToLinear(0.5f), 1e-4f);
            for (int i = 0; i <= 255; i++)
            {
                float g = i / 255f;
                Assert.AreEqual(g, BlendMath.LinearToGamma(BlendMath.GammaToLinear(g)), 1e-4f, "value " + i);
            }
        }

        // ── ノーマル ──

        private static void AssertVector(Vector3 expected, Vector3 actual, float eps = 1e-4f)
        {
            Assert.AreEqual(expected.x, actual.x, eps, "x");
            Assert.AreEqual(expected.y, actual.y, eps, "y");
            Assert.AreEqual(expected.z, actual.z, eps, "z");
        }

        [Test]
        public void Rnm_FlatDetailKeepsBase()
        {
            var b = NormalMath.SafeNormalize(new Vector3(0.3f, -0.4f, 0.8f));
            AssertVector(b, NormalMath.SafeNormalize(NormalMath.Rnm(b, NormalMath.Flat)));
            AssertVector(b, NormalMath.BlendTexture(b, NormalMath.Flat, 1f));
        }

        [Test]
        public void Rnm_FlatBaseGivesDetail()
        {
            var d = NormalMath.SafeNormalize(new Vector3(-0.2f, 0.5f, 0.7f));
            AssertVector(d, NormalMath.SafeNormalize(NormalMath.Rnm(NormalMath.Flat, d)));
        }

        [Test]
        public void BlendTexture_ZeroCoverageKeepsBaseAndResultIsUnitLength()
        {
            var b = NormalMath.SafeNormalize(new Vector3(0.3f, 0.1f, 0.9f));
            var d = NormalMath.SafeNormalize(new Vector3(-0.5f, 0.2f, 0.6f));
            AssertVector(b, NormalMath.BlendTexture(b, d, 0f));
            foreach (float a in new[] { 0.25f, 0.5f, 1f })
                Assert.AreEqual(1f, NormalMath.BlendTexture(b, d, a).magnitude, 1e-4f);
        }

        [Test]
        public void BlendFlatten_ReplacesTowardFlat()
        {
            var b = NormalMath.SafeNormalize(new Vector3(0.6f, 0f, 0.8f));
            AssertVector(NormalMath.Flat, NormalMath.BlendFlatten(b, 1f));
            AssertVector(b, NormalMath.BlendFlatten(b, 0f));
            var half = NormalMath.BlendFlatten(b, 0.5f);
            Assert.Greater(half.x, 0f);
            Assert.Less(half.x, b.x);
            Assert.AreEqual(1f, half.magnitude, 1e-4f);
        }

        [Test]
        public void Prepare_ScalesAndRotates()
        {
            var n = NormalMath.SafeNormalize(new Vector3(0.5f, 0f, 0.8f));
            AssertVector(NormalMath.Flat, NormalMath.Prepare(n, 0f, 0f));
            var stronger = NormalMath.Prepare(n, 2f, 0f);
            Assert.Greater(stronger.x, n.x);

            var rotated = NormalMath.Prepare(n, 1f, 90f);
            Assert.AreEqual(0f, rotated.x, 1e-4f);
            Assert.AreEqual(n.x, rotated.y, 1e-4f);
            Assert.AreEqual(n.z, rotated.z, 1e-4f);
        }

        [Test]
        public void EncodeDecode_RoundTrip()
        {
            var n = NormalMath.SafeNormalize(new Vector3(0.2f, -0.7f, 0.5f));
            var e = NormalMath.Encode(n);
            AssertVector(n, NormalMath.Decode(e.x, e.y, e.z));
            AssertVector(NormalMath.Flat, NormalMath.SafeNormalize(Vector3.zero));
        }

        // ── UV ──

        [Test]
        public void Tiling_IdentityAndKnownCases()
        {
            var id = new UvTransform();
            var uv = new Vector2(0.3f, 0.8f);
            Assert.AreEqual(uv.x, UvMath.Tiling(uv, id).x, Eps);
            Assert.AreEqual(uv.y, UvMath.Tiling(uv, id).y, Eps);

            var t = new UvTransform { tiling = new Vector2(2f, 4f), offset = new Vector2(0.1f, 0.2f) };
            var r = UvMath.Tiling(new Vector2(0.75f, 0.5f), t);
            Assert.AreEqual(0.5f + 0.5f + 0.1f, r.x, Eps);
            Assert.AreEqual(0.5f + 0.2f, r.y, Eps);

            // 中心は回転しても動かない
            var rot = new UvTransform { rotation = 37f };
            var c = UvMath.Tiling(new Vector2(0.5f, 0.5f), rot);
            Assert.AreEqual(0.5f, c.x, Eps);
            Assert.AreEqual(0.5f, c.y, Eps);
            var q = UvMath.Tiling(new Vector2(1f, 0.5f), new UvTransform { rotation = 90f });
            Assert.AreEqual(0.5f, q.x, Eps);
            Assert.AreEqual(1f, q.y, Eps);
        }

        [Test]
        public void Decal_MapsCenterAndBounds()
        {
            var center = new Vector2(0.3f, 0.6f);
            var size = new Vector2(0.2f, 0.1f);
            var mid = UvMath.Decal(center, center, size, 25f, out bool inside);
            Assert.IsTrue(inside);
            Assert.AreEqual(0.5f, mid.x, Eps);
            Assert.AreEqual(0.5f, mid.y, Eps);

            var near = UvMath.Decal(new Vector2(0.39f, 0.64f), center, size, 0f, out inside);
            Assert.IsTrue(inside);
            Assert.AreEqual(0.95f, near.x, 1e-4f);
            Assert.AreEqual(0.9f, near.y, 1e-4f);

            UvMath.Decal(new Vector2(0.45f, 0.6f), center, size, 0f, out inside);
            Assert.IsFalse(inside);
        }

        [Test]
        public void Affine_MatchesTilingAndDecalFormulas()
        {
            var layer = new Layer
            {
                baseXform = new UvTransform { tiling = new Vector2(3f, 2f), offset = new Vector2(0.15f, -0.4f), rotation = 33f },
                syncNormal = false,
                normalXform = new UvTransform { tiling = new Vector2(5f, 7f), offset = new Vector2(-0.2f, 0.3f), rotation = -70f },
                decalCenter = new Vector2(0.35f, 0.7f),
                decalSize = new Vector2(0.3f, 0.12f),
                decalRotation = 48f,
            };
            var samples = new[] { new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.2f, 0.9f), new Vector2(0.77f, 0.13f) };

            layer.placementMode = PlacementMode.Tiling;
            foreach (bool normal in new[] { false, true })
            {
                UvMath.Affine(layer, normal, out var a, out var t);
                foreach (var uv in samples)
                {
                    var expected = UvMath.Tiling(uv, normal ? layer.normalXform : layer.baseXform);
                    var actual = UvMath.Apply(a, t, uv);
                    Assert.AreEqual(expected.x, actual.x, 1e-4f);
                    Assert.AreEqual(expected.y, actual.y, 1e-4f);
                }
            }

            layer.placementMode = PlacementMode.Decal;
            UvMath.Affine(layer, false, out var da, out var dt);
            foreach (var uv in samples)
            {
                var expected = UvMath.Decal(uv, layer.decalCenter, layer.decalSize, layer.decalRotation, out _);
                var actual = UvMath.Apply(da, dt, uv);
                Assert.AreEqual(expected.x, actual.x, 1e-3f);
                Assert.AreEqual(expected.y, actual.y, 1e-3f);
            }
        }

        [Test]
        public void Affine_SyncedNormalUsesBaseTransform()
        {
            var layer = new Layer
            {
                baseXform = new UvTransform { tiling = new Vector2(3f, 2f), rotation = 20f },
                syncNormal = true,
                normalXform = new UvTransform { tiling = new Vector2(9f, 9f) },
            };
            UvMath.Affine(layer, false, out var a0, out var t0);
            UvMath.Affine(layer, true, out var a1, out var t1);
            Assert.AreEqual(a0, a1);
            Assert.AreEqual(t0, t1);
            Assert.AreEqual(-20f, UvMath.NormalRotation(layer), Eps);
            layer.placementMode = PlacementMode.Decal;
            layer.decalRotation = 15f;
            Assert.AreEqual(15f, UvMath.NormalRotation(layer), Eps);
        }
    }
}
