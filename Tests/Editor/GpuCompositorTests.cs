using System.Collections.Generic;
using Dennoko.DTXTool.Core;
using NUnit.Framework;
using UnityEngine;

namespace Dennoko.DTXTool.Tests
{
    /// <summary>
    /// 合成シェーダー（Compose.shader）の結果を、CPU の基準実装（BlendMath / NormalMath / UvMath）と突き合わせる。
    /// 作業セッションは使わず、マスクとテクスチャはメモリ上に作ったものを渡す。
    /// </summary>
    public sealed class GpuCompositorTests
    {
        private const int Size = 16;

        private sealed class FakeSource : ICompositeSource
        {
            public readonly Dictionary<string, Texture> Masks = new Dictionary<string, Texture>();
            public readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
            public Texture DefaultMask;

            public Texture MaskFor(Layer layer, int size) => Masks.TryGetValue(layer.id, out var m) ? m : DefaultMask;
            public void DoneWithMask(Layer layer, Texture mask) { }
            public Texture2D Resolve(TextureRef reference)
                => reference != null && Textures.TryGetValue(reference.guid ?? string.Empty, out var t) ? t : null;
        }

        private GpuResources _gpu;
        private Compositor _compositor;
        private FakeSource _source;
        private readonly List<Object> _owned = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            Assert.IsTrue(GpuResources.TryCreate(out _gpu, out string error), error);
            _compositor = new Compositor(_gpu);
            _source = new FakeSource { DefaultMask = Solid(255, 255, 255, 255, true) };
        }

        [TearDown]
        public void TearDown()
        {
            _compositor?.Dispose();
            _gpu?.Dispose();
            foreach (var o in _owned) if (o != null) Object.DestroyImmediate(o);
            _owned.Clear();
        }

        // ── 道具 ──

        private Texture2D Make(int w, int h, Color32[] pixels, bool linear)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, linear) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            t.SetPixels32(pixels);
            t.Apply(false, false);
            _owned.Add(t);
            return t;
        }

        private Texture2D Solid(byte r, byte g, byte b, byte a, bool linear)
        {
            var px = new Color32[4];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(r, g, b, a);
            return Make(2, 2, px, linear);
        }

        /// <summary>左半分 = left、右半分 = right の値を持つマスク。</summary>
        private Texture2D SplitMask(byte left, byte right)
        {
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    byte v = x < Size / 2 ? left : right;
                    px[y * Size + x] = new Color32(v, v, v, 255);
                }
            return Make(Size, Size, px, true);
        }

        private TextureRef Register(string key, Texture2D texture)
        {
            _source.Textures[key] = texture;
            return new TextureRef { guid = key };
        }

        private static DtxDocument Doc(Color background)
        {
            var doc = new DtxDocument();
            DocumentOps.SetSize(doc, Size);
            doc.background.color = background;
            return doc;
        }

        private static Layer AddLayer(DtxDocument doc, Color color)
        {
            var l = new Layer { id = Layer.NewId(), name = "L" + doc.layers.Count, color = color };
            doc.layers.Add(l);
            return l;
        }

        private Color32 BasePixel(int x, int y)
        {
            var bytes = GpuReadback.ReadRGBA32(_compositor.BaseResult);
            int i = (y * Size + x) * 4;
            return new Color32(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
        }

        private Vector3 NormalPixel(int x, int y)
        {
            var encoded = _gpu.CreateRT("TestNormal", Size, Size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            _compositor.EncodeNormal(_compositor.NormalResult, encoded);
            var bytes = GpuReadback.ReadRGBA32(encoded);
            _gpu.Release(encoded);
            int i = (y * Size + x) * 4;
            Assert.AreEqual(255, bytes[i + 3], "normal alpha must be 1 so that UnpackNormal reads it");
            return NormalMath.Decode(bytes[i] / 255f, bytes[i + 1] / 255f, bytes[i + 2] / 255f);
        }

        private static void AssertColor(Color expected, Color32 actual, int tolerance = 2, string message = null)
        {
            Assert.AreEqual(expected.r * 255f, actual.r, tolerance, "r " + message);
            Assert.AreEqual(expected.g * 255f, actual.g, tolerance, "g " + message);
            Assert.AreEqual(expected.b * 255f, actual.b, tolerance, "b " + message);
            Assert.AreEqual(expected.a * 255f, actual.a, tolerance, "a " + message);
        }

        private static void AssertNormal(Vector3 expected, Vector3 actual, float tolerance = 0.03f, string message = null)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, "x " + message);
            Assert.AreEqual(expected.y, actual.y, tolerance, "y " + message);
            Assert.AreEqual(expected.z, actual.z, tolerance, "z " + message);
        }

        private static Color32 Encode(Vector3 n)
        {
            var e = NormalMath.Encode(NormalMath.SafeNormalize(n));
            return new Color32((byte)Mathf.RoundToInt(e.x * 255f), (byte)Mathf.RoundToInt(e.y * 255f), (byte)Mathf.RoundToInt(e.z * 255f), 255);
        }

        private Texture2D NormalTexture(Vector3 n)
        {
            var c = Encode(n);
            return Solid(c.r, c.g, c.b, c.a, true);
        }

        private static Vector3 Quantized(Vector3 n)
        {
            var c = Encode(n);
            return NormalMath.Decode(c.r / 255f, c.g / 255f, c.b / 255f);
        }

        // ── Base ──

        [Test]
        public void Base_SolidBackgroundIsStoredAsGammaValues()
        {
            var bg = new Color(0.2f, 0.4f, 0.6f, 1f);
            _compositor.Render(Doc(bg), Size, _source);
            AssertColor(bg, BasePixel(3, 5), 1);
            Assert.AreEqual(Size, _compositor.Size);
        }

        [TestCase(BlendMode.Normal)]
        [TestCase(BlendMode.Multiply)]
        [TestCase(BlendMode.Additive)]
        [TestCase(BlendMode.Screen)]
        [TestCase(BlendMode.Overlay)]
        public void Base_BlendModesMatchCpuReference(BlendMode mode)
        {
            var bg = new Color(0.3f, 0.6f, 0.8f, 1f);
            var src = new Color(0.7f, 0.5f, 0.25f, 1f);
            var doc = Doc(bg);
            var layer = AddLayer(doc, src);
            layer.blendMode = mode;
            layer.opacity = 0.6f;
            _source.DefaultMask = Solid(128, 128, 128, 255, true);

            _compositor.Render(doc, Size, _source);

            float a = BlendMath.Coverage(128f / 255f, 0.6f, 1f);
            AssertColor(BlendMath.Composite(mode, bg, src, a), BasePixel(7, 9), 2, mode.ToString());
        }

        [Test]
        public void Base_MaskLimitsWhereTheLayerApplies()
        {
            var bg = new Color(0f, 0f, 1f, 1f);
            var src = new Color(1f, 0f, 0f, 1f);
            var doc = Doc(bg);
            var layer = AddLayer(doc, src);
            _source.Masks[layer.id] = SplitMask(255, 0);

            _compositor.Render(doc, Size, _source);

            AssertColor(src, BasePixel(2, 8), 1, "inside the mask");
            AssertColor(bg, BasePixel(13, 8), 1, "outside the mask");
        }

        [Test]
        public void Base_LayersStackBottomToTopAndHiddenLayersAreSkipped()
        {
            var doc = Doc(Color.black);
            AddLayer(doc, new Color(1f, 0f, 0f, 1f));
            var top = AddLayer(doc, new Color(0f, 1f, 0f, 1f));
            var hidden = AddLayer(doc, new Color(1f, 1f, 1f, 1f));
            hidden.visible = false;

            _compositor.Render(doc, Size, _source);
            AssertColor(new Color(0f, 1f, 0f, 1f), BasePixel(4, 4), 1, "top visible layer wins");

            top.opacity = 0f;
            _compositor.Render(doc, Size, _source);
            AssertColor(new Color(1f, 0f, 0f, 1f), BasePixel(4, 4), 1, "zero opacity is skipped");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Base_TextureFillKeepsThePixelValuesOfTheSourceImage(bool linearTexture)
        {
            // sRGB テクスチャはリニアのプロジェクトで読むとリニア値になるが、合成はガンマ値で行うので元の値に戻っていること
            var doc = Doc(Color.black);
            var layer = AddLayer(doc, Color.white);
            layer.fillKind = FillKind.Texture;
            layer.baseTexture = Register("tex", Solid(200, 100, 50, 255, linearTexture));

            _compositor.Render(doc, Size, _source);
            AssertColor(new Color(200 / 255f, 100 / 255f, 50 / 255f, 1f), BasePixel(6, 6), 2, QualitySettings.activeColorSpace.ToString());
        }

        [Test]
        public void Base_TintMultipliesAndTextureAlphaScalesCoverage()
        {
            var bg = new Color(0f, 0f, 0f, 1f);
            var doc = Doc(bg);
            var layer = AddLayer(doc, Color.white);
            layer.fillKind = FillKind.Texture;
            layer.baseTexture = Register("tex", Solid(200, 200, 200, 128, false));
            layer.tint = new Color(0.5f, 1f, 1f, 1f);

            _compositor.Render(doc, Size, _source);

            var src = new Color(200 / 255f * 0.5f, 200 / 255f, 200 / 255f, 1f);
            AssertColor(BlendMath.Composite(BlendMode.Normal, bg, src, 128f / 255f), BasePixel(5, 5), 2);
        }

        [Test]
        public void Base_MissingTextureLeavesTheLayerOut()
        {
            var bg = new Color(0.1f, 0.2f, 0.3f, 1f);
            var doc = Doc(bg);
            var layer = AddLayer(doc, Color.red);
            layer.fillKind = FillKind.Texture;
            layer.baseTexture = new TextureRef { guid = "missing" };

            _compositor.Render(doc, Size, _source);
            AssertColor(bg, BasePixel(5, 5), 1);
        }

        [Test]
        public void Base_BackgroundTextureAlphaIsPreserved()
        {
            var doc = Doc(Color.white);
            doc.background.baseKind = BackgroundBaseKind.Texture;
            doc.background.baseTexture = Register("bg", Solid(10, 20, 30, 64, false));
            AddLayer(doc, new Color(1f, 1f, 1f, 1f));

            _compositor.Render(doc, Size, _source);
            var p = BasePixel(8, 8);
            Assert.AreEqual(255, p.r, 1);
            Assert.AreEqual(64, p.a, 1, "layers change RGB only");

            doc.layers.Clear();
            _compositor.Render(doc, Size, _source);
            AssertColor(new Color(10 / 255f, 20 / 255f, 30 / 255f, 64 / 255f), BasePixel(8, 8), 2);
        }

        [Test]
        public void Base_TilingAndRotationMatchCpuReference()
        {
            // 2×2 の 4 色テクスチャ（ポイントサンプリング）を、いくつかの変形で貼って画素ごとに照合する
            var colors = new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255), new Color32(255, 255, 0, 255) };
            var tex = Make(2, 2, colors, true);
            var doc = Doc(Color.black);
            var layer = AddLayer(doc, Color.white);
            layer.fillKind = FillKind.Texture;
            layer.baseTexture = Register("tex", tex);

            var cases = new[]
            {
                new UvTransform(),
                new UvTransform { tiling = new Vector2(2f, 1f) },
                new UvTransform { tiling = new Vector2(1f, 2f), offset = new Vector2(0.25f, 0f) },
                new UvTransform { rotation = 90f },
                new UvTransform { tiling = new Vector2(2f, 2f), rotation = 180f },
            };
            foreach (var xform in cases)
            {
                layer.baseXform = xform;
                _compositor.Render(doc, Size, _source);
                var bytes = GpuReadback.ReadRGBA32(_compositor.BaseResult);
                int checkedPixels = 0;
                for (int y = 0; y < Size; y++)
                {
                    for (int x = 0; x < Size; x++)
                    {
                        var uv = UvMath.Tiling(new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size), xform);
                        float fu = Mathf.Repeat(uv.x, 1f), fv = Mathf.Repeat(uv.y, 1f);
                        // テクセルの境界に近い画素は丸めの向きで結果が変わるので照合しない
                        if (Mathf.Abs(fu - 0.5f) < 0.02f || Mathf.Abs(fv - 0.5f) < 0.02f || fu < 0.02f || fu > 0.98f || fv < 0.02f || fv > 0.98f) continue;
                        var expected = colors[(fv < 0.5f ? 0 : 2) + (fu < 0.5f ? 0 : 1)];
                        int i = (y * Size + x) * 4;
                        string where = "xform t=" + xform.tiling + " o=" + xform.offset + " r=" + xform.rotation + " at " + x + "," + y;
                        Assert.AreEqual(expected.r, bytes[i], 1, where);
                        Assert.AreEqual(expected.g, bytes[i + 1], 1, where);
                        Assert.AreEqual(expected.b, bytes[i + 2], 1, where);
                        checkedPixels++;
                    }
                }
                Assert.Greater(checkedPixels, Size * Size / 2);
            }
        }

        [Test]
        public void Base_DecalOnlyCoversItsRectangle()
        {
            var bg = new Color(0f, 0f, 1f, 1f);
            var doc = Doc(bg);
            var layer = AddLayer(doc, Color.white);
            layer.fillKind = FillKind.Texture;
            layer.baseTexture = Register("tex", Solid(255, 0, 0, 255, true));
            layer.placementMode = PlacementMode.Decal;
            layer.decalCenter = new Vector2(0.5f, 0.5f);
            layer.decalSize = new Vector2(0.5f, 0.25f);

            _compositor.Render(doc, Size, _source);

            AssertColor(Color.red, BasePixel(8, 8), 1, "center");
            AssertColor(Color.red, BasePixel(5, 7), 1, "inside");
            AssertColor(bg, BasePixel(2, 8), 1, "left of the decal");
            AssertColor(bg, BasePixel(8, 3), 1, "below the decal");
            AssertColor(bg, BasePixel(8, 12), 1, "above the decal");

            // 単色のレイヤーはデカールにしてもマスク全体に効く（範囲はテクスチャにだけ意味がある）
            layer.fillKind = FillKind.Color;
            layer.color = Color.green;
            _compositor.Render(doc, Size, _source);
            AssertColor(Color.green, BasePixel(2, 8), 1);
        }

        // ── Normal ──

        [Test]
        public void Normal_FlatBackgroundAndLayersWithoutNormalStayFlat()
        {
            var doc = Doc(Color.white);
            AddLayer(doc, Color.red);
            _compositor.Render(doc, Size, _source);
            AssertNormal(NormalMath.Flat, NormalPixel(4, 4), 0.01f);
        }

        [Test]
        public void Normal_TextureLayerUsesRnmOverTheBackground()
        {
            var nb = new Vector3(0.3f, -0.2f, 0.9f);
            var nl = new Vector3(-0.4f, 0.3f, 0.8f);
            var doc = Doc(Color.white);
            doc.background.normalKind = BackgroundNormalKind.Texture;
            doc.background.normalTexture = Register("bgn", NormalTexture(nb));
            var layer = AddLayer(doc, Color.white);
            layer.normalKind = NormalKind.Texture;
            layer.normalTexture = Register("ln", NormalTexture(nl));
            layer.opacity = 0.7f;

            _compositor.Render(doc, Size, _source);

            var expected = NormalMath.BlendTexture(Quantized(nb), Quantized(nl), 0.7f);
            AssertNormal(expected, NormalPixel(9, 3));

            // ベースだけを持たないレイヤーでも、ノーマルは合成される
            layer.fillKind = FillKind.Texture;
            layer.baseTexture = new TextureRef { guid = "missing" };
            _compositor.Render(doc, Size, _source);
            AssertNormal(expected, NormalPixel(9, 3));
        }

        [Test]
        public void Normal_ScaleAndUvRotationAreApplied()
        {
            var nl = new Vector3(0.5f, 0f, 0.85f);
            var doc = Doc(Color.white);
            var layer = AddLayer(doc, Color.white);
            layer.normalKind = NormalKind.Texture;
            layer.normalTexture = Register("ln", NormalTexture(nl));
            layer.normalScale = 0.5f;
            layer.baseXform = new UvTransform { rotation = 90f };

            _compositor.Render(doc, Size, _source);

            var prepared = NormalMath.Prepare(Quantized(nl), 0.5f, UvMath.NormalRotation(layer));
            AssertNormal(prepared, NormalPixel(4, 11));
            Assert.Less(Mathf.Abs(prepared.x), 0.02f, "rotating the UV by 90 degrees turns x into y");
            Assert.Less(prepared.y, -0.1f);
        }

        [Test]
        public void Normal_FlattenReplacesInsideTheMaskOnly()
        {
            var nb = new Vector3(0.5f, 0.3f, 0.8f);
            var doc = Doc(Color.white);
            doc.background.normalKind = BackgroundNormalKind.Texture;
            doc.background.normalTexture = Register("bgn", NormalTexture(nb));
            var layer = AddLayer(doc, Color.white);
            layer.normalKind = NormalKind.Flatten;
            _source.Masks[layer.id] = SplitMask(255, 0);

            _compositor.Render(doc, Size, _source);
            AssertNormal(NormalMath.Flat, NormalPixel(3, 8), 0.02f, "flattened");
            AssertNormal(Quantized(nb), NormalPixel(12, 8), 0.02f, "untouched");

            layer.opacity = 0.5f;
            _compositor.Render(doc, Size, _source);
            AssertNormal(NormalMath.BlendFlatten(Quantized(nb), 0.5f), NormalPixel(3, 8));
        }

        // ── Present / Raster / Pad ──

        [Test]
        public void Present_GivesTheMaterialTheSamePixelValues()
        {
            var bg = new Color(0.25f, 0.5f, 0.75f, 0.5f);
            var doc = Doc(bg);
            _compositor.Render(doc, Size, _source);

            var baseRt = _gpu.CreateRT("PB", Size, Size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, FilterMode.Bilinear, TextureWrapMode.Repeat);
            var normalRt = _gpu.CreateRT("PN", Size, Size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, FilterMode.Bilinear, TextureWrapMode.Repeat);
            _compositor.Present(baseRt, normalRt);

            // sRGB の RenderTexture に入った値（＝マテリアルが sRGB テクスチャとして読む値）が、合成結果のガンマ値と同じ
            var b = GpuReadback.ReadRGBA32(baseRt);
            AssertColor(bg, new Color32(b[0], b[1], b[2], b[3]), 2);
            var n = GpuReadback.ReadRGBA32(normalRt);
            Assert.AreEqual(128, n[0], 1);
            Assert.AreEqual(128, n[1], 1);
            Assert.AreEqual(255, n[2], 1);
            Assert.AreEqual(255, n[3]);
        }

        [Test]
        public void Raster_WritesStraightAlphaLayerContentForPsd()
        {
            var layer = new Layer { id = "a", color = new Color(0.2f, 0.4f, 0.6f, 0.5f) };
            var rt = _gpu.CreateRT("R", Size, Size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);

            Assert.IsTrue(_compositor.RasterBase(layer, _source, rt));
            var b = GpuReadback.ReadRGBA32(rt);
            AssertColor(layer.color, new Color32(b[0], b[1], b[2], b[3]), 1, "not premultiplied, mask and opacity not applied");
            Assert.IsFalse(_compositor.RasterNormal(layer, _source, rt), "no normal on this layer");

            layer.normalKind = NormalKind.Flatten;
            Assert.IsTrue(_compositor.RasterNormal(layer, _source, rt));
            b = GpuReadback.ReadRGBA32(rt);
            Assert.AreEqual(128, b[0], 1);
            Assert.AreEqual(255, b[2], 1);
            Assert.AreEqual(255, b[3]);

            layer.fillKind = FillKind.Texture;
            layer.baseTexture = new TextureRef { guid = "missing" };
            Assert.IsFalse(_compositor.RasterBase(layer, _source, rt));
        }

        [Test]
        public void Pad_ExtendsCoveredPixelsOutwardByTheGivenWidth()
        {
            // 左半分だけ面が使っている（赤）。右半分は使っていない（青）
            var colorPx = new Color32[Size * Size];
            var coverPx = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    bool covered = x < Size / 2;
                    colorPx[y * Size + x] = covered ? new Color32(255, 0, 0, 200) : new Color32(0, 0, 255, 100);
                    coverPx[y * Size + x] = covered ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
                }
            var color = _gpu.CreateRT("C", Size, Size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            var coverage = _gpu.CreateMaskRT("V", Size, Size, TextureWrapMode.Clamp);
            Graphics.Blit(Make(Size, Size, colorPx, true), color);
            Graphics.Blit(Make(Size, Size, coverPx, true), coverage);

            var padded = TextureBaker.Pad(_gpu, color, coverage, 3, RenderTextureFormat.ARGB32);
            var b = GpuReadback.ReadRGBA32(padded);
            Color32 At(int x, int y) => new Color32(b[(y * Size + x) * 4], b[(y * Size + x) * 4 + 1], b[(y * Size + x) * 4 + 2], b[(y * Size + x) * 4 + 3]);

            Assert.AreEqual(255, At(7, 8).r, "covered pixels are untouched");
            for (int x = 8; x <= 10; x++)
            {
                Assert.AreEqual(255, At(x, 8).r, 1, "padded column " + x);
                Assert.AreEqual(0, At(x, 8).b, 1, "padded column " + x);
                Assert.AreEqual(100, At(x, 8).a, 1, "alpha is not padded");
            }
            Assert.AreEqual(0, At(11, 8).r, 1, "beyond the padding width");
            Assert.AreEqual(255, At(11, 8).b, 1);

            var none = TextureBaker.Pad(_gpu, color, coverage, 0, RenderTextureFormat.ARGB32);
            var nb = GpuReadback.ReadRGBA32(none);
            Assert.AreEqual(0, nb[(8 * Size + 8) * 4], "zero width leaves the image as is");
        }
    }
}
