using System;
using System.IO;
using Dennoko.DTXTool.Core;
using NUnit.Framework;

namespace Dennoko.DTXTool.Tests
{
    /// <summary>自前の PSD ライター / リーダー（Core）。書いたものを読み戻して構造と画素を確かめる。</summary>
    public sealed class CorePsdTests
    {
        private const int W = 8, H = 4;

        /// <summary>行 0 = 上端の RGBA。画素 (x, y) の値が位置で決まるので、上下の取り違えを検出できる。</summary>
        private static byte[] Pattern(int seed)
        {
            var p = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    p[i] = (byte)(seed + x * 10);
                    p[i + 1] = (byte)(seed + y * 40);
                    p[i + 2] = (byte)(x == y ? 255 : 0);
                    p[i + 3] = (byte)(255 - x * 8);
                }
            }
            return p;
        }

        private static byte[] MaskPattern()
        {
            var m = new byte[W * H];
            for (int i = 0; i < m.Length; i++) m[i] = (byte)(i < W ? 255 : i * 3);
            return m;
        }

        private static byte[] FlipRows(byte[] src, int stride)
        {
            var dst = new byte[src.Length];
            int row = W * stride;
            for (int y = 0; y < H; y++) Buffer.BlockCopy(src, y * row, dst, (H - 1 - y) * row, row);
            return dst;
        }

        private static byte[] WriteSample(byte[] payload, out byte[] layerA, out byte[] layerB, out byte[] mask, out byte[] merged)
        {
            layerA = Pattern(1);
            layerB = Pattern(60);
            mask = MaskPattern();
            merged = Pattern(100);
            using (var ms = new MemoryStream())
            using (var w = new PsdWriter(W, H))
            {
                w.BeginGroup();
                w.AddLayer("Background", true, 1f, PsdWriter.BlendNormal, layerA, null, false);
                w.AddLayer("布地 Cloth", false, 0.5f, PsdWriter.BlendMultiply, layerB, mask, false);
                w.EndGroup("BaseMap", true);
                w.BeginGroup();
                w.AddLayer("Background", true, 1f, PsdWriter.BlendNormal, layerA, null, false);
                w.EndGroup("NormalMap", false, false);
                if (payload != null) w.AddImageResource(PsdReader.DtxResourceId, PsdReader.DtxResourceName, payload);
                w.Write(ms, merged, false);
                return ms.ToArray();
            }
        }

        [Test]
        public void Header_IsStandard8BitRgb()
        {
            var file = WriteSample(null, out _, out _, out _, out _);
            Assert.AreEqual((byte)'8', file[0]);
            Assert.AreEqual((byte)'B', file[1]);
            Assert.AreEqual((byte)'P', file[2]);
            Assert.AreEqual((byte)'S', file[3]);
            Assert.AreEqual(0, file[4]);
            Assert.AreEqual(1, file[5], "PSD version 1");
            for (int i = 6; i < 12; i++) Assert.AreEqual(0, file[i], "reserved");

            var psd = PsdReader.Read(file, false);
            Assert.AreEqual(W, psd.Width);
            Assert.AreEqual(H, psd.Height);
            Assert.AreEqual(3, psd.Channels);
            Assert.AreEqual(8, psd.Depth);
            Assert.AreEqual(3, psd.ColorMode, "RGB");
        }

        [Test]
        public void Layers_KeepNamesGroupsBlendOpacityAndVisibility()
        {
            var file = WriteSample(null, out _, out _, out _, out _);
            var psd = PsdReader.Read(file, false);
            Assert.AreEqual(7, psd.Layers.Count);

            // ファイル上は下から上の順。グループは「終端 → 中身 → フォルダ」
            Assert.AreEqual(3, psd.Layers[0].SectionType);
            Assert.AreEqual("Background", psd.Layers[1].Name);
            Assert.AreEqual(0, psd.Layers[1].SectionType);
            Assert.IsTrue(psd.Layers[1].Visible);
            Assert.AreEqual(255, psd.Layers[1].Opacity);
            Assert.AreEqual("norm", psd.Layers[1].BlendKey);
            Assert.IsFalse(psd.Layers[1].HasMask);

            var cloth = psd.Layers[2];
            Assert.AreEqual("布地 Cloth", cloth.Name, "unicode name");
            Assert.IsFalse(cloth.Visible);
            Assert.AreEqual(128, cloth.Opacity);
            Assert.AreEqual("mul ", cloth.BlendKey);
            Assert.IsTrue(cloth.HasMask);
            Assert.AreEqual(0, cloth.Top);
            Assert.AreEqual(0, cloth.Left);
            Assert.AreEqual(H, cloth.Bottom);
            Assert.AreEqual(W, cloth.Right);

            Assert.AreEqual("BaseMap", psd.Layers[3].Name);
            Assert.AreEqual(1, psd.Layers[3].SectionType, "open folder");
            Assert.IsTrue(psd.Layers[3].Visible);
            Assert.AreEqual("pass", psd.Layers[3].BlendKey);

            Assert.AreEqual(3, psd.Layers[4].SectionType);
            Assert.AreEqual("Background", psd.Layers[5].Name);
            Assert.AreEqual("NormalMap", psd.Layers[6].Name);
            Assert.AreEqual(2, psd.Layers[6].SectionType, "closed folder");
            Assert.IsFalse(psd.Layers[6].Visible, "NormalMap folder starts hidden");
        }

        [Test]
        public void Pixels_RoundTripForLayersMasksAndMergedImage()
        {
            var file = WriteSample(null, out var layerA, out var layerB, out var mask, out var merged);
            var psd = PsdReader.Read(file, true);
            CollectionAssert.AreEqual(layerA, psd.Layers[1].Rgba);
            Assert.IsNull(psd.Layers[1].Mask);
            CollectionAssert.AreEqual(layerB, psd.Layers[2].Rgba);
            CollectionAssert.AreEqual(mask, psd.Layers[2].Mask);
            Assert.IsNull(psd.Layers[3].Rgba, "folders carry no pixels");

            Assert.AreEqual(W * H * 3, psd.MergedRgb.Length);
            for (int i = 0; i < W * H; i++)
            {
                Assert.AreEqual(merged[i * 4], psd.MergedRgb[i * 3]);
                Assert.AreEqual(merged[i * 4 + 1], psd.MergedRgb[i * 3 + 1]);
                Assert.AreEqual(merged[i * 4 + 2], psd.MergedRgb[i * 3 + 2]);
            }
        }

        [Test]
        public void BottomUpInput_IsFlippedToTopDown()
        {
            var topDown = Pattern(7);
            var maskTopDown = MaskPattern();
            byte[] file;
            using (var ms = new MemoryStream())
            using (var w = new PsdWriter(W, H))
            {
                w.AddLayer("L", true, 1f, PsdWriter.BlendNormal, FlipRows(topDown, 4), FlipRows(maskTopDown, 1), true);
                w.Write(ms, FlipRows(topDown, 4), true);
                file = ms.ToArray();
            }
            var psd = PsdReader.Read(file, true);
            CollectionAssert.AreEqual(topDown, psd.Layers[0].Rgba);
            CollectionAssert.AreEqual(maskTopDown, psd.Layers[0].Mask);
            Assert.AreEqual(topDown[0], psd.MergedRgb[0]);
            Assert.AreEqual(topDown[(H - 1) * W * 4 + 1], psd.MergedRgb[(H - 1) * W * 3 + 1]);
        }

        [Test]
        public void ScratchStream_ProducesIdenticalFile()
        {
            var inMemory = WriteSample(new byte[] { 1, 2, 3 }, out var a, out var b, out var mask, out var merged);
            string path = Path.GetTempFileName();
            try
            {
                using (var scratch = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                using (var ms = new MemoryStream())
                using (var w = new PsdWriter(W, H, scratch))
                {
                    w.BeginGroup();
                    w.AddLayer("Background", true, 1f, PsdWriter.BlendNormal, a, null, false);
                    w.AddLayer("布地 Cloth", false, 0.5f, PsdWriter.BlendMultiply, b, mask, false);
                    w.EndGroup("BaseMap", true);
                    w.BeginGroup();
                    w.AddLayer("Background", true, 1f, PsdWriter.BlendNormal, a, null, false);
                    w.EndGroup("NormalMap", false, false);
                    w.AddImageResource(PsdReader.DtxResourceId, PsdReader.DtxResourceName, new byte[] { 1, 2, 3 });
                    w.Write(ms, merged, false);
                    CollectionAssert.AreEqual(inMemory, ms.ToArray());
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Payload_SurvivesRoundTripThroughImageResource()
        {
            var doc = CoreDocumentTests.SampleDocument();
            var payload = DocumentCodec.Encode(doc);
            var file = WriteSample(payload, out _, out _, out _, out _);

            CollectionAssert.AreEqual(payload, PsdReader.ReadDtxPayload(file));
            using (var ms = new MemoryStream(file))
                CollectionAssert.AreEqual(payload, PsdReader.ReadDtxPayload(ms), "stream reader reads only the head of the file");
            CoreDocumentTests.AssertSame(doc, DocumentCodec.Decode(PsdReader.ReadDtxPayload(file)));

            // 奇数長のリソースの後ろでも、レイヤーを正しく読める（偶数境界への詰め物）
            var odd = WriteSample(new byte[] { (byte)'D', (byte)'T', (byte)'X', (byte)'M', 9 }, out _, out _, out _, out _);
            Assert.AreEqual(7, PsdReader.Read(odd, true).Layers.Count);
            Assert.AreEqual(5, PsdReader.ReadDtxPayload(odd).Length);
        }

        [Test]
        public void Reader_RejectsNonPsd()
        {
            Assert.AreEqual(PsdReadError.NotPsd, Assert.Throws<PsdReadException>(() => PsdReader.Read(new byte[64], false)).Error);
            Assert.AreEqual(PsdReadError.NotPsd, Assert.Throws<PsdReadException>(() => PsdReader.Read(null, false)).Error);
            using (var ms = new MemoryStream(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34 }))
                Assert.AreEqual(PsdReadError.NotPsd, Assert.Throws<PsdReadException>(() => PsdReader.ReadDtxPayload(ms)).Error);

            // PSB（バージョン 2）は対象外
            var file = WriteSample(null, out _, out _, out _, out _);
            file[5] = 2;
            Assert.AreEqual(PsdReadError.NotPsd, Assert.Throws<PsdReadException>(() => PsdReader.Read(file, false)).Error);
        }

        [Test]
        public void Reader_RejectsPsdWithoutDtxData()
        {
            var plain = WriteSample(null, out _, out _, out _, out _);
            Assert.AreEqual(PsdReadError.NoDtxData, Assert.Throws<PsdReadException>(() => PsdReader.ReadDtxPayload(plain)).Error);

            // 同じ ID でも中身が DTXTool のものでなければ対象外
            var foreign = WriteSample(new byte[] { 1, 2, 3, 4, 5, 6 }, out _, out _, out _, out _);
            Assert.AreEqual(PsdReadError.NoDtxData, Assert.Throws<PsdReadException>(() => PsdReader.ReadDtxPayload(foreign)).Error);
        }

        [Test]
        public void Reader_ReportsTruncatedFileAsCorrupted()
        {
            var file = WriteSample(DocumentCodec.Encode(new DtxDocument()), out _, out _, out _, out _);
            var cut = new byte[file.Length / 2];
            Array.Copy(file, cut, cut.Length);
            Assert.AreEqual(PsdReadError.Corrupted, Assert.Throws<PsdReadException>(() => PsdReader.Read(cut, true)).Error);
            var head = new byte[40];
            Array.Copy(file, head, head.Length);
            using (var ms = new MemoryStream(head))
                Assert.AreEqual(PsdReadError.Corrupted, Assert.Throws<PsdReadException>(() => PsdReader.ReadDtxPayload(ms)).Error);
        }

        [Test]
        public void Writer_ValidatesInput()
        {
            Assert.Throws<ArgumentException>(() => new PsdWriter(0, 4));
            Assert.Throws<ArgumentException>(() => new PsdWriter(4, 30001));
            using (var w = new PsdWriter(W, H))
            {
                Assert.Throws<ArgumentException>(() => w.AddLayer("x", true, 1f, PsdWriter.BlendNormal, new byte[3], null, false));
                Assert.Throws<ArgumentException>(() => w.AddLayer("x", true, 1f, PsdWriter.BlendNormal, Pattern(0), new byte[3], false));
                Assert.Throws<ArgumentException>(() => w.AddLayer("x", true, 1f, "toolong", Pattern(0), null, false));
                Assert.Throws<InvalidOperationException>(() => w.EndGroup("g", true));
                w.BeginGroup();
                Assert.Throws<InvalidOperationException>(() => w.Write(new MemoryStream(), Pattern(0), false), "unclosed group");
            }
        }

        [Test]
        public void BlendKeys_MapBothWays()
        {
            foreach (BlendMode mode in Enum.GetValues(typeof(BlendMode)))
            {
                string key = PsdWriter.BlendKey(mode);
                Assert.AreEqual(4, key.Length);
                Assert.IsTrue(PsdWriter.TryParseBlendKey(key, out var parsed));
                Assert.AreEqual(mode, parsed);
            }
            Assert.AreEqual("lddg", PsdWriter.BlendKey(BlendMode.Additive));
            Assert.IsFalse(PsdWriter.TryParseBlendKey("xxxx", out _));
        }

        // ── PackBits ──

        [Test]
        public void PackBits_RoundTripsRunsLiteralsAndLongRows()
        {
            var rng = new System.Random(12345);
            foreach (int length in new[] { 1, 2, 3, 127, 128, 129, 300, 4096 })
            {
                foreach (int kind in new[] { 0, 1, 2, 3 })
                {
                    var row = new byte[length];
                    for (int i = 0; i < length; i++)
                    {
                        switch (kind)
                        {
                            case 0: row[i] = 77; break;                              // 同じ値だけ
                            case 1: row[i] = (byte)rng.Next(256); break;             // ばらばら
                            case 2: row[i] = (byte)(i / 5 % 2 == 0 ? 10 : rng.Next(256)); break;
                            default: row[i] = (byte)(i % 2); break;                  // 2 個続きが無い
                        }
                    }
                    using (var ms = new MemoryStream())
                    {
                        int written = PackBits.EncodeRow(row, 0, length, 1, ms);
                        Assert.AreEqual(ms.Length, written);
                        Assert.LessOrEqual(written, length + length / 128 + 1, "worst case bound");
                        var packed = ms.ToArray();
                        var back = new byte[length];
                        int pos = 0;
                        Assert.IsTrue(PackBits.DecodeRow(packed, ref pos, packed.Length, back, 0, length), "length " + length + " kind " + kind);
                        CollectionAssert.AreEqual(row, back);
                    }
                }
            }
        }

        [Test]
        public void PackBits_CompressesFlatRowsAndHonorsStride()
        {
            var rgba = new byte[1024 * 4];
            for (int i = 0; i < 1024; i++)
            {
                rgba[i * 4] = 200;
                rgba[i * 4 + 1] = (byte)i;
            }
            using (var ms = new MemoryStream())
            {
                int n = PackBits.EncodeRow(rgba, 0, 1024, 4, ms);
                Assert.AreEqual(16, n, "1024 equal bytes = 8 runs of 128");
            }
            using (var ms = new MemoryStream())
            {
                PackBits.EncodeRow(rgba, 1, 1024, 4, ms);
                var packed = ms.ToArray();
                var back = new byte[1024];
                int pos = 0;
                Assert.IsTrue(PackBits.DecodeRow(packed, ref pos, packed.Length, back, 0, 1024));
                for (int i = 0; i < 1024; i++) Assert.AreEqual((byte)i, back[i]);
            }
        }

        [Test]
        public void PackBits_DecodeRejectsBrokenData()
        {
            var back = new byte[8];
            int pos = 0;
            Assert.IsFalse(PackBits.DecodeRow(new byte[] { 7, 1, 2 }, ref pos, 3, back, 0, 8), "literal longer than the data");
            pos = 0;
            Assert.IsFalse(PackBits.DecodeRow(new byte[] { 0xF0, 5 }, ref pos, 2, back, 0, 8), "run longer than the row");
            pos = 0;
            Assert.IsFalse(PackBits.DecodeRow(new byte[] { 0, 5 }, ref pos, 2, back, 0, 8), "row too short");
        }
    }
}
