using System;
using Dennoko.DTXTool.Core;
using NUnit.Framework;
using UnityEngine;

namespace Dennoko.DTXTool.Tests
{
    /// <summary>ドキュメントのシリアライズと書き換え（Core。GPU も作業セッションも使わない）。</summary>
    public sealed class CoreDocumentTests
    {
        internal static DtxDocument SampleDocument()
        {
            var doc = new DtxDocument { width = 1024, height = 1024, uvChannel = 1, meshHash = -1234567890123L, triangleCount = 70, submeshIndex = 2, targetName = "Body 体" };
            doc.background.baseKind = BackgroundBaseKind.Texture;
            doc.background.color = new Color(0.1f, 0.2f, 0.3f, 0.4f);
            doc.background.baseTexture = new TextureRef { guid = "0123456789abcdef0123456789abcdef", path = "Assets/Tex/base.png" };
            doc.background.normalKind = BackgroundNormalKind.Texture;
            doc.background.normalTexture = new TextureRef { guid = "fedcba9876543210fedcba9876543210", path = "Assets/Tex/normal.png" };

            var a = new Layer
            {
                id = Layer.NewId(),
                name = "布 Cloth",
                visible = false,
                opacity = 0.75f,
                blendMode = BlendMode.Overlay,
                color = new Color(0.9f, 0.8f, 0.7f, 0.6f),
                baseTexture = new TextureRef { guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", path = "Assets/Tex/cloth.png" },
                normalTexture = new TextureRef { guid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", path = "Assets/Tex/cloth_n.png" },
                normalScale = 1.5f,
                replaceNormal = true,
                normalMask = new TextureRef { guid = "cccccccccccccccccccccccccccccccc", path = "Assets/Tex/cloth_nmask.png" },
                placementMode = PlacementMode.Decal,
                baseXform = new UvTransform { tiling = new Vector2(4f, 2f), offset = new Vector2(0.25f, -0.5f), rotation = 30f },
                syncNormal = false,
                normalXform = new UvTransform { tiling = new Vector2(8f, 8f), offset = new Vector2(0.1f, 0.2f), rotation = -45f },
                decalCenter = new Vector2(0.3f, 0.6f),
                decalSize = new Vector2(0.2f, 0.1f),
                decalRotation = 12.5f,
            };
            a.mask.source = MaskSource.Selection;
            a.mask.selectedBits = new[] { 0x0F0F0F0F, unchecked((int)0x80000001), 63 };
            a.mask.paintRle = new byte[] { 1, 2, 3, 4, 5 };
            a.mask.embedded = MaskRaster.Compress(new byte[16 * 16]);
            a.mask.embeddedSize = 16;
            a.mask.invert = true;
            a.mask.opacity = 0.5f;
            a.mask.feather = 0.0125f;
            doc.layers.Add(a);

            var b = new Layer { id = Layer.NewId(), name = "Image", replaceNormal = true, syncNormal = true };
            b.mask.source = MaskSource.Image;
            b.mask.image = new TextureRef { guid = "cccccccccccccccccccccccccccccccc", path = "Assets/Tex/mask.png" };
            doc.layers.Add(b);
            return doc;
        }

        internal static void AssertSame(DtxDocument e, DtxDocument a)
        {
            e.EnsureVariations();
            a.EnsureVariations();
            Assert.AreEqual(e.editMode, a.editMode);
            Assert.AreEqual(e.width, a.width);
            Assert.AreEqual(e.height, a.height);
            Assert.AreEqual(e.uvChannel, a.uvChannel);
            Assert.AreEqual(e.meshHash, a.meshHash);
            Assert.AreEqual(e.triangleCount, a.triangleCount);
            Assert.AreEqual(e.submeshIndex, a.submeshIndex);
            Assert.AreEqual(e.targetName, a.targetName);
            Assert.AreEqual(e.activeVariationIndex, a.activeVariationIndex);
            Assert.AreEqual(e.variations.Count, a.variations.Count);
            for (int v = 0; v < e.variations.Count; v++)
            {
                var ev = e.variations[v];
                var av = a.variations[v];
                Assert.AreEqual(ev.id, av.id);
                Assert.AreEqual(ev.name, av.name);
                Assert.AreEqual(ev.background.baseKind, av.background.baseKind);
                Assert.AreEqual(ev.background.color, av.background.color);
                AssertSame(ev.background.baseTexture, av.background.baseTexture);
                Assert.AreEqual(ev.background.normalKind, av.background.normalKind);
                AssertSame(ev.background.normalTexture, av.background.normalTexture);
                Assert.AreEqual(ev.layers.Count, av.layers.Count);
                for (int i = 0; i < ev.layers.Count; i++) AssertSame(ev.layers[i], av.layers[i]);
            }
        }

        internal static void AssertSame(Layer e, Layer a)
        {
            Assert.AreEqual(e.id, a.id);
            Assert.AreEqual(e.name, a.name);
            Assert.AreEqual(e.visible, a.visible);
            Assert.AreEqual(e.opacity, a.opacity);
            Assert.AreEqual(e.blendMode, a.blendMode);
            Assert.AreEqual(e.mask.source, a.mask.source);
            CollectionAssert.AreEqual(e.mask.selectedBits, a.mask.selectedBits);
            CollectionAssert.AreEqual(e.mask.paintRle, a.mask.paintRle);
            AssertSame(e.mask.image, a.mask.image);
            CollectionAssert.AreEqual(e.mask.embedded, a.mask.embedded);
            Assert.AreEqual(e.mask.embeddedSize, a.mask.embeddedSize);
            Assert.AreEqual(e.mask.invert, a.mask.invert);
            Assert.AreEqual(e.mask.opacity, a.mask.opacity);
            Assert.AreEqual(e.mask.feather, a.mask.feather);
            Assert.AreEqual(e.color, a.color);
            AssertSame(e.baseTexture, a.baseTexture);
            AssertSame(e.normalTexture, a.normalTexture);
            Assert.AreEqual(e.normalScale, a.normalScale);
            Assert.AreEqual(e.replaceNormal, a.replaceNormal);
            AssertSame(e.normalMask, a.normalMask);
            Assert.AreEqual(-1, a.legacyFillKind, "decoded layers are always in the current form");
            Assert.AreEqual(-1, a.legacyNormalKind);
            Assert.AreEqual(e.placementMode, a.placementMode);
            AssertSame(e.baseXform, a.baseXform);
            Assert.AreEqual(e.syncNormal, a.syncNormal);
            AssertSame(e.normalXform, a.normalXform);
            Assert.AreEqual(e.decalCenter, a.decalCenter);
            Assert.AreEqual(e.decalSize, a.decalSize);
            Assert.AreEqual(e.decalRotation, a.decalRotation);
        }

        private static void AssertSame(TextureRef e, TextureRef a)
        {
            Assert.AreEqual(e.guid, a.guid);
            Assert.AreEqual(e.path, a.path);
        }

        private static void AssertSame(UvTransform e, UvTransform a)
        {
            Assert.AreEqual(e.tiling, a.tiling);
            Assert.AreEqual(e.offset, a.offset);
            Assert.AreEqual(e.rotation, a.rotation);
        }

        // ── 旧形式からの移行 ──

        [Test]
        public void MigrateLegacy_TurnsOldChoicesIntoTheCurrentForm()
        {
            var tex = new TextureRef { guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            var normal = new TextureRef { guid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };

            // 単色 + なし: 使っていなかったテクスチャは外す。同じ配置だったならノーマル側へ写す
            var color = new Layer { legacyFillKind = 0, legacyNormalKind = 0, color = Color.red, baseTexture = tex.Clone(), normalTexture = normal.Clone(), syncNormal = true };
            color.baseXform.tiling = new Vector2(3f, 3f);
            Assert.IsTrue(DocumentOps.MigrateLegacy(color));
            Assert.IsFalse(color.HasBaseTexture);
            Assert.IsFalse(color.HasNormalTexture);
            Assert.AreEqual(Color.red, color.color);
            Assert.IsFalse(color.syncNormal);
            Assert.AreEqual(new Vector2(3f, 3f), color.normalXform.tiling);

            // テクスチャ × Tint + マップ: Tint が色になり、ノーマルは RNM で重ねる
            var textured = new Layer { legacyFillKind = 1, legacyNormalKind = 2, color = Color.red, legacyTint = Color.gray, baseTexture = tex.Clone(), normalTexture = normal.Clone() };
            Assert.IsTrue(DocumentOps.MigrateLegacy(textured));
            Assert.IsTrue(textured.HasBaseTexture);
            Assert.AreEqual(Color.gray, textured.color);
            Assert.IsTrue(textured.HasNormalTexture);
            Assert.IsFalse(textured.replaceNormal);

            // 平坦化: テクスチャを外して凹凸を消す
            var flatten = new Layer { legacyFillKind = 0, legacyNormalKind = 1, normalTexture = normal.Clone() };
            Assert.IsTrue(DocumentOps.MigrateLegacy(flatten));
            Assert.IsFalse(flatten.HasNormalTexture);
            Assert.IsTrue(flatten.replaceNormal);
            Assert.IsTrue(flatten.AffectsNormal);

            // 移行済みのものは触らない
            Assert.IsFalse(DocumentOps.MigrateLegacy(flatten));
            Assert.IsFalse(DocumentOps.MigrateLegacy(new Layer()));
        }

        // ── DocumentCodec ──

        [Test]
        public void Codec_RoundTripsEveryField()
        {
            var doc = SampleDocument();
            var decoded = DocumentCodec.Decode(DocumentCodec.Encode(doc));
            AssertSame(doc, decoded);
            Assert.AreEqual(DocumentCodec.FormatVersion, decoded.formatVersion);
        }

        [Test]
        public void Codec_RoundTripsEmptyDocument()
        {
            var doc = new DtxDocument();
            AssertSame(doc, DocumentCodec.Decode(DocumentCodec.Encode(doc)));
        }

        [Test]
        public void Codec_RejectsForeignData()
        {
            var e = Assert.Throws<DocumentCodecException>(() => DocumentCodec.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 }));
            Assert.AreEqual(DocumentCodecError.NotDtx, e.Error);
            Assert.AreEqual(DocumentCodecError.NotDtx, Assert.Throws<DocumentCodecException>(() => DocumentCodec.Decode(null)).Error);
            Assert.IsFalse(DocumentCodec.HasMagic(new byte[] { (byte)'D', (byte)'T' }));
        }

        [Test]
        public void Codec_DetectsCorruption()
        {
            var data = DocumentCodec.Encode(SampleDocument());
            var flipped = (byte[])data.Clone();
            flipped[data.Length / 2] ^= 0x55;
            Assert.AreEqual(DocumentCodecError.Corrupted, Assert.Throws<DocumentCodecException>(() => DocumentCodec.Decode(flipped)).Error);

            var truncated = new byte[data.Length - 9];
            Array.Copy(data, truncated, truncated.Length);
            Assert.AreEqual(DocumentCodecError.Corrupted, Assert.Throws<DocumentCodecException>(() => DocumentCodec.Decode(truncated)).Error);
        }

        [Test]
        public void Codec_RejectsNewerFormat()
        {
            var data = DocumentCodec.Encode(SampleDocument());
            data[4] = (byte)(DocumentCodec.FormatVersion + 1);
            Assert.AreEqual(DocumentCodecError.TooNew, Assert.Throws<DocumentCodecException>(() => DocumentCodec.Decode(data)).Error);
        }

        [Test]
        public void Codec_CloneLayerIsDeepAndHashTracksContent()
        {
            var layer = SampleDocument().layers[0];
            var copy = DocumentCodec.CloneLayer(layer);
            AssertSame(layer, copy);
            Assert.AreNotSame(layer.mask.selectedBits, copy.mask.selectedBits);
            Assert.AreEqual(DocumentCodec.Hash(layer), DocumentCodec.Hash(copy));
            copy.mask.selectedBits[0] ^= 1;
            Assert.AreNotEqual(DocumentCodec.Hash(layer), DocumentCodec.Hash(copy));
            Assert.AreEqual(0x0F0F0F0F, layer.mask.selectedBits[0]);
        }

        // ── DocumentOps ──

        [Test]
        public void Ops_AddSelectionLayerCopiesSelectionAndGoesOnTop()
        {
            var doc = new DtxDocument();
            var bits = new[] { 5, 0 };
            var first = DocumentOps.AddSelectionLayer(doc, "Layer", bits, new byte[] { 9 });
            var second = DocumentOps.AddSelectionLayer(doc, "Layer", bits, null);
            bits[0] = 99;
            Assert.AreEqual(5, first.mask.selectedBits[0], "selection must be copied");
            Assert.AreEqual("Layer", first.name);
            Assert.AreEqual("Layer 2", second.name);
            Assert.AreSame(second, doc.layers[1]);
            Assert.AreNotEqual(first.id, second.id);
            Assert.AreEqual(MaskSource.Selection, first.mask.source);
            Assert.AreEqual(0, second.mask.paintRle.Length);
        }

        [Test]
        public void Ops_AddImageMaskLayer()
        {
            var doc = new DtxDocument();
            var l = DocumentOps.AddImageMaskLayer(doc, "mask", new TextureRef { guid = "g", path = "p" });
            Assert.AreEqual(MaskSource.Image, l.mask.source);
            Assert.AreEqual("g", l.mask.image.guid);
            Assert.AreEqual(1, doc.layers.Count);
        }

        [Test]
        public void Ops_DuplicateInsertsAboveWithNewId()
        {
            var doc = SampleDocument();
            string id = doc.layers[0].id;
            var copy = DocumentOps.Duplicate(doc, id);
            Assert.AreEqual(3, doc.layers.Count);
            Assert.AreSame(copy, doc.layers[1]);
            Assert.AreNotEqual(id, copy.id);
            Assert.AreEqual("布 Cloth 2", copy.name);
            Assert.AreEqual(doc.layers[0].opacity, copy.opacity);
            Assert.IsNull(DocumentOps.Duplicate(doc, "missing"));
        }

        [Test]
        public void Ops_DeleteReturnsNextSelection()
        {
            var doc = SampleDocument();
            string a = doc.layers[0].id, b = doc.layers[1].id;
            Assert.AreEqual(a, DocumentOps.Delete(doc, b), "deleting the top selects the one below");
            Assert.AreEqual(string.Empty, DocumentOps.Delete(doc, a));
            Assert.AreEqual(0, doc.layers.Count);
            Assert.AreEqual(string.Empty, DocumentOps.Delete(doc, "missing"));
        }

        [Test]
        public void Ops_MoveClampsAndReportsChange()
        {
            var doc = SampleDocument();
            DocumentOps.AddSelectionLayer(doc, "C", null, null);
            string a = doc.layers[0].id;
            Assert.IsTrue(DocumentOps.Move(doc, a, 99));
            Assert.AreEqual(a, doc.layers[2].id);
            Assert.IsFalse(DocumentOps.Move(doc, a, 2));
            Assert.IsTrue(DocumentOps.Move(doc, a, -5));
            Assert.AreEqual(a, doc.layers[0].id);
            Assert.IsFalse(DocumentOps.Move(doc, "missing", 0));
        }

        [Test]
        public void Ops_AdaptToMesh_SameMeshKeepsSelectionAndDropsBackup()
        {
            var doc = SampleDocument();
            doc.triangleCount = 70;
            int lost = DocumentOps.AdaptToMesh(doc, doc.meshHash, 70);
            Assert.AreEqual(0, lost);
            Assert.AreEqual(MaskSource.Selection, doc.layers[0].mask.source);
            Assert.AreEqual(0x0F0F0F0F, doc.layers[0].mask.selectedBits[0]);
            Assert.IsFalse(doc.layers[0].mask.HasEmbedded);
            Assert.AreEqual(MaskSource.Image, doc.layers[1].mask.source);
        }

        [Test]
        public void Ops_AdaptToMesh_ChangedMeshFallsBackToEmbeddedRaster()
        {
            var doc = SampleDocument();
            int lost = DocumentOps.AdaptToMesh(doc, 42L, 200);
            Assert.AreEqual(1, lost);
            var m = doc.layers[0].mask;
            Assert.AreEqual(MaskSource.Embedded, m.source);
            Assert.IsTrue(m.HasEmbedded);
            Assert.AreEqual(TriangleBits.WordCount(200), m.selectedBits.Length);
            Assert.IsFalse(TriangleBits.AnySet(m.selectedBits));
            Assert.AreEqual(42L, doc.meshHash);
            Assert.AreEqual(200, doc.triangleCount);
        }

        [Test]
        public void Ops_AdaptToMesh_ChangedMeshWithoutBackupBecomesEmptySelection()
        {
            var doc = SampleDocument();
            doc.layers[0].mask.embedded = Array.Empty<byte>();
            doc.layers[0].mask.embeddedSize = 0;
            Assert.AreEqual(1, DocumentOps.AdaptToMesh(doc, 42L, 40));
            Assert.AreEqual(MaskSource.Selection, doc.layers[0].mask.source);
            Assert.AreEqual(2, doc.layers[0].mask.selectedBits.Length);
        }

        [TestCase(100, 512)]
        [TestCase(700, 512)]
        [TestCase(800, 1024)]
        [TestCase(2048, 2048)]
        [TestCase(3500, 4096)]
        [TestCase(9000, 4096)]
        public void Ops_NearestSize(int pixels, int expected)
        {
            Assert.AreEqual(expected, DocumentOps.NearestSize(pixels));
        }

        [Test]
        public void Ops_ContributesAndFeather()
        {
            var l = new Layer();
            Assert.IsTrue(DocumentOps.Contributes(l));
            l.visible = false;
            Assert.IsFalse(DocumentOps.Contributes(l));
            l.visible = true;
            l.opacity = 0f;
            Assert.IsFalse(DocumentOps.Contributes(l));
            l.opacity = 1f;
            l.mask.opacity = 0f;
            Assert.IsFalse(DocumentOps.Contributes(l));
            Assert.IsFalse(DocumentOps.Contributes(null));

            l.mask.feather = 8f / 2048f;
            Assert.AreEqual(8, DocumentOps.FeatherPixels(l.mask, 2048));
            Assert.AreEqual(4, DocumentOps.FeatherPixels(l.mask, 1024));
            l.mask.feather = 10f;
            Assert.AreEqual(256, DocumentOps.FeatherPixels(l.mask, 2048), "clamped");
        }

        [Test]
        public void Document_FindAndUniqueName()
        {
            var doc = SampleDocument();
            Assert.AreSame(doc.layers[1], doc.Find(doc.layers[1].id));
            Assert.IsNull(doc.Find("missing"));
            Assert.IsNull(doc.Find(null));
            Assert.AreEqual("New", doc.UniqueName("New"));
            Assert.AreEqual("Image 2", doc.UniqueName("Image"));
            Assert.AreEqual("Layer", doc.UniqueName(""));
        }

        [Test]
        public void Ops_VariationsDuplicateSelectRenameDeleteAndRoundTrip()
        {
            var doc = SampleDocument();
            doc.editMode = EditMode.BaseOnly;
            Assert.AreEqual(1, doc.variations.Count);
            Assert.AreEqual("Variation 1", doc.ActiveVariation.name);
            string firstLayerId = doc.layers[0].id;

            // 追加は直前のバリエーションを複製し、新しいレイヤー ID を振る
            var v2 = DocumentOps.AddVariation(doc);
            Assert.AreEqual(2, doc.variations.Count);
            Assert.AreEqual(1, doc.activeVariationIndex);
            Assert.AreEqual("Variation 2", v2.name);
            Assert.AreEqual(doc.variations[0].layers.Count, v2.layers.Count);
            Assert.AreNotEqual(firstLayerId, v2.layers[0].id, "cloned layers must have fresh IDs");
            Assert.AreEqual(doc.variations[0].background.color, v2.background.color);

            // バリエーション 2 の色だけ変えてもバリエーション 1 は変わらない
            v2.layers[0].color = Color.cyan;
            v2.background.color = Color.magenta;
            Assert.AreNotEqual(Color.cyan, doc.variations[0].layers[0].color);
            Assert.AreNotEqual(Color.magenta, doc.variations[0].background.color);

            // 名前変更・重複回避
            Assert.IsTrue(DocumentOps.RenameVariation(doc, 1, "Red Variant"));
            Assert.AreEqual("Red Variant", doc.variations[1].name);
            var v3 = DocumentOps.AddVariation(doc);
            Assert.AreEqual("Red Variant 2", v3.name);

            // Codec ラウンドトリップ（複数バリエーション + editMode）
            var decoded = DocumentCodec.Decode(DocumentCodec.Encode(doc));
            AssertSame(doc, decoded);

            // 全バリエーションが AdaptToMesh の対象になる
            int lost = DocumentOps.AdaptToMesh(decoded, 999L, 128);
            Assert.AreEqual(3, lost, "one selection layer in each of the 3 variations");
            foreach (var v in decoded.variations)
                Assert.AreEqual(MaskSource.Embedded, v.layers[0].mask.source);

            // 切替と削除（最後の 1 件は消せない）
            Assert.IsTrue(DocumentOps.SelectVariation(doc, 0));
            Assert.AreEqual(0, doc.activeVariationIndex);
            Assert.IsTrue(DocumentOps.DeleteVariation(doc, 2));
            Assert.IsTrue(DocumentOps.DeleteVariation(doc, 1));
            Assert.AreEqual(1, doc.variations.Count);
            Assert.IsFalse(DocumentOps.DeleteVariation(doc, 0), "cannot delete the last remaining variation");
        }

        // ── MaskRaster ──

        [Test]
        public void MaskRaster_CompressRoundTripAndResize()
        {
            var pixels = new byte[32 * 32];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)((i % 32) < 16 ? 255 : 0);
            var packed = MaskRaster.Compress(pixels);
            Assert.Less(packed.Length, pixels.Length);
            CollectionAssert.AreEqual(pixels, MaskRaster.Decompress(packed, 32));
            Assert.IsNull(MaskRaster.Decompress(packed, 64), "wrong size");
            Assert.IsNull(MaskRaster.Decompress(new byte[] { 1, 2, 3 }, 32), "garbage");
            Assert.IsNull(MaskRaster.Decompress(null, 32));

            var half = MaskRaster.Resize(pixels, 32, 16);
            Assert.AreEqual(16 * 16, half.Length);
            Assert.AreEqual(255, half[0]);
            Assert.AreEqual(0, half[15]);
            Assert.AreSame(pixels, MaskRaster.Resize(pixels, 32, 32));
        }
    }
}
