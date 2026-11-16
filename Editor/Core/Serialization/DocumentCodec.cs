using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace Dennoko.DTXTool.Core
{
    public enum DocumentCodecError
    {
        /// <summary>DTXTool のデータではない。</summary>
        NotDtx,
        /// <summary>途中で切れている・チェックサムが合わない。</summary>
        Corrupted,
        /// <summary>このバージョンより新しい形式。</summary>
        TooNew,
    }

    public sealed class DocumentCodecException : Exception
    {
        public DocumentCodecError Error { get; }

        public DocumentCodecException(DocumentCodecError error, string message) : base(message)
        {
            Error = error;
        }
    }

    /// <summary>
    /// DtxDocument ⇄ バイト列。PSD の復元情報と自動保存の両方で使う。
    ///   "DTXM" | u16 形式バージョン | u32 本体の長さ | 本体（Deflate） | u64 本体の FNV-1a
    /// レイヤーなどのまとまりは長さ付きのチャンクにしてあり、後のバージョンが末尾に項目を足しても古い読み手が読み飛ばせる。
    /// </summary>
    public static class DocumentCodec
    {
        public const int FormatVersion = DtxDocument.CurrentFormatVersion;
        private static readonly byte[] Magic = { (byte)'D', (byte)'T', (byte)'X', (byte)'M' };
        private const int HeaderSize = 4 + 2 + 4;
        private const int MaxBodySize = 1 << 30;

        public static bool HasMagic(byte[] data)
            => data != null && data.Length >= 4 && data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3];

        public static byte[] Encode(DtxDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            byte[] body;
            using (var raw = new MemoryStream())
            {
                using (var deflate = new DeflateStream(raw, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new BinaryWriter(deflate, Encoding.UTF8, true))
                {
                    WriteDocument(w, doc);
                }
                body = raw.ToArray();
            }

            using (var ms = new MemoryStream(body.Length + HeaderSize + 8))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write((ushort)FormatVersion);
                w.Write((uint)body.Length);
                w.Write(body);
                w.Write(Fnv1a.Of(body));
                w.Flush();
                return ms.ToArray();
            }
        }

        public static DtxDocument Decode(byte[] data)
        {
            if (!HasMagic(data)) throw new DocumentCodecException(DocumentCodecError.NotDtx, "not a DTXTool document");
            if (data.Length < HeaderSize + 8) throw new DocumentCodecException(DocumentCodecError.Corrupted, "truncated header");
            int version = data[4] | (data[5] << 8);
            long length = BitConverter.ToUInt32(data, 6);
            if (version > FormatVersion)
                throw new DocumentCodecException(DocumentCodecError.TooNew, "format version " + version + " is newer than " + FormatVersion);
            if (length > MaxBodySize || HeaderSize + length + 8 > data.Length)
                throw new DocumentCodecException(DocumentCodecError.Corrupted, "truncated body");

            var body = new byte[length];
            Buffer.BlockCopy(data, HeaderSize, body, 0, (int)length);
            ulong sum = BitConverter.ToUInt64(data, HeaderSize + (int)length);
            if (sum != Fnv1a.Of(body)) throw new DocumentCodecException(DocumentCodecError.Corrupted, "checksum mismatch");

            try
            {
                using (var ms = new MemoryStream(body))
                using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
                using (var plain = new MemoryStream())
                {
                    deflate.CopyTo(plain);
                    plain.Position = 0;
                    using (var r = new BinaryReader(plain, Encoding.UTF8))
                    {
                        var doc = ReadDocument(r);
                        doc.formatVersion = FormatVersion;
                        return doc;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException
                                      || e is ArgumentException || e is OverflowException)
            {
                throw new DocumentCodecException(DocumentCodecError.Corrupted, "broken body: " + e.Message);
            }
        }

        /// <summary>複製（Undo 用のシリアライズと同じ内容になる）。</summary>
        public static DtxDocument Clone(DtxDocument doc) => Decode(Encode(doc));

        public static Layer CloneLayer(Layer layer)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) WriteLayer(w, layer);
                ms.Position = 0;
                using (var r = new BinaryReader(ms, Encoding.UTF8)) return ReadLayer(r);
            }
        }

        /// <summary>内容のハッシュ（Undo で書き戻されたかの判定や、キャッシュのキーに使う）。</summary>
        public static ulong Hash(Layer layer)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) WriteLayer(w, layer);
                return Fnv1a.Of(ms.ToArray());
            }
        }

        // ── 書き込み ──

        private static void WriteDocument(BinaryWriter w, DtxDocument d)
        {
            Chunk(w, c =>
            {
                c.Write(d.width);
                c.Write(d.height);
                c.Write(d.uvChannel);
                c.Write(d.meshHash);
                c.Write(d.triangleCount);
                c.Write(d.submeshIndex);
                c.Write(d.targetName ?? string.Empty);
            });
            var bg = d.background ?? new Background();
            Chunk(w, c =>
            {
                c.Write((int)bg.baseKind);
                Write(c, bg.color);
                Write(c, bg.baseTexture);
                c.Write((int)bg.normalKind);
                Write(c, bg.normalTexture);
            });
            w.Write(d.layers != null ? d.layers.Count : 0);
            if (d.layers != null)
                foreach (var l in d.layers) WriteLayer(w, l);
        }

        private static void WriteLayer(BinaryWriter w, Layer l)
        {
            Chunk(w, c =>
            {
                c.Write(l.id ?? string.Empty);
                c.Write(l.name ?? string.Empty);
                c.Write(l.visible);
                c.Write(l.opacity);
                c.Write((int)l.blendMode);

                var m = l.mask ?? new MaskSpec();
                Chunk(c, mc =>
                {
                    mc.Write((int)m.source);
                    Write(mc, m.selectedBits);
                    Write(mc, m.paintRle);
                    Write(mc, m.image);
                    Write(mc, m.embedded);
                    mc.Write(m.embeddedSize);
                    mc.Write(m.invert);
                    mc.Write(m.opacity);
                    mc.Write(m.feather);
                });

                // ベースとノーマルの種類は旧形式の並びのまま書く（古い DTXTool でもおおむね同じ見た目で読める）。
                // 旧: 種類 0 = 単色 / 1 = テクスチャ × Tint。今の「テクスチャ × 色」は Tint に色を入れれば同じになる
                c.Write(l.HasBaseTexture ? 1 : 0);
                Write(c, l.color);
                Write(c, l.baseTexture);
                Write(c, l.color);

                // 旧: 0 = なし / 1 = 平坦化 / 2 = マップ（置き換えは旧形式に無いのでマップとして書く）
                c.Write(l.HasNormalTexture ? 2 : l.replaceNormal ? 1 : 0);
                Write(c, l.normalTexture);
                c.Write(l.normalScale);

                c.Write((int)l.placementMode);
                Write(c, l.baseXform);
                c.Write(l.syncNormal);
                Write(c, l.normalXform);
                Write(c, l.decalCenter);
                Write(c, l.decalSize);
                c.Write(l.decalRotation);

                // ここから今の形式（これがあれば、上の種類は読まずに今の意味で読む）
                c.Write(l.replaceNormal);
            });
        }

        private static void Chunk(BinaryWriter w, Action<BinaryWriter> write)
        {
            using (var ms = new MemoryStream())
            {
                using (var c = new BinaryWriter(ms, Encoding.UTF8, true)) write(c);
                w.Write((int)ms.Length);
                w.Write(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        private static void Write(BinaryWriter w, Color c)
        {
            w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
        }

        private static void Write(BinaryWriter w, Vector2 v)
        {
            w.Write(v.x); w.Write(v.y);
        }

        private static void Write(BinaryWriter w, TextureRef t)
        {
            w.Write(t?.guid ?? string.Empty);
            w.Write(t?.path ?? string.Empty);
        }

        private static void Write(BinaryWriter w, UvTransform t)
        {
            t ??= new UvTransform();
            Write(w, t.tiling);
            Write(w, t.offset);
            w.Write(t.rotation);
        }

        private static void Write(BinaryWriter w, int[] a)
        {
            int n = a?.Length ?? 0;
            w.Write(n);
            for (int i = 0; i < n; i++) w.Write(a[i]);
        }

        private static void Write(BinaryWriter w, byte[] a)
        {
            int n = a?.Length ?? 0;
            w.Write(n);
            if (n > 0) w.Write(a);
        }

        // ── 読み込み ──

        /// <summary>チャンクの中身。項目が尽きたら既定値を返す（古い形式に無い項目）。</summary>
        private sealed class ChunkReader : IDisposable
        {
            private readonly MemoryStream _ms;
            public readonly BinaryReader R;

            public ChunkReader(BinaryReader parent)
            {
                int length = parent.ReadInt32();
                if (length < 0) throw new InvalidDataException("negative chunk length");
                var bytes = parent.ReadBytes(length);
                if (bytes.Length != length) throw new EndOfStreamException();
                _ms = new MemoryStream(bytes);
                R = new BinaryReader(_ms, Encoding.UTF8);
            }

            public bool More => _ms.Position < _ms.Length;

            public int Int(int fallback = 0) => More ? R.ReadInt32() : fallback;
            public long Long(long fallback = 0) => More ? R.ReadInt64() : fallback;
            public float Float(float fallback = 0f) => More ? R.ReadSingle() : fallback;
            public bool Bool(bool fallback = false) => More ? R.ReadBoolean() : fallback;
            public string String() => More ? R.ReadString() : string.Empty;

            public Color Color(Color fallback)
                => More ? new Color(R.ReadSingle(), R.ReadSingle(), R.ReadSingle(), R.ReadSingle()) : fallback;

            public Vector2 Vector2(Vector2 fallback)
                => More ? new Vector2(R.ReadSingle(), R.ReadSingle()) : fallback;

            public TextureRef Texture()
                => More ? new TextureRef { guid = R.ReadString(), path = R.ReadString() } : new TextureRef();

            public UvTransform Transform()
            {
                if (!More) return new UvTransform();
                return new UvTransform
                {
                    tiling = new Vector2(R.ReadSingle(), R.ReadSingle()),
                    offset = new Vector2(R.ReadSingle(), R.ReadSingle()),
                    rotation = R.ReadSingle(),
                };
            }

            public int[] Ints()
            {
                if (!More) return Array.Empty<int>();
                int n = R.ReadInt32();
                if (n < 0 || (long)n * 4 > _ms.Length - _ms.Position) throw new InvalidDataException("bad int array");
                var a = new int[n];
                for (int i = 0; i < n; i++) a[i] = R.ReadInt32();
                return a;
            }

            public byte[] Bytes()
            {
                if (!More) return Array.Empty<byte>();
                int n = R.ReadInt32();
                if (n < 0 || n > _ms.Length - _ms.Position) throw new InvalidDataException("bad byte array");
                return n == 0 ? Array.Empty<byte>() : R.ReadBytes(n);
            }

            public void Dispose()
            {
                R.Dispose();
                _ms.Dispose();
            }
        }

        private static DtxDocument ReadDocument(BinaryReader r)
        {
            var d = new DtxDocument();
            using (var c = new ChunkReader(r))
            {
                d.width = c.Int(d.width);
                d.height = c.Int(d.height);
                d.uvChannel = c.Int();
                d.meshHash = c.Long();
                d.triangleCount = c.Int();
                d.submeshIndex = c.Int(-1);
                d.targetName = c.String();
            }
            if (d.width < 1 || d.height < 1 || d.width > 16384 || d.height > 16384) throw new InvalidDataException("bad size");
            using (var c = new ChunkReader(r))
            {
                var bg = d.background;
                bg.baseKind = (BackgroundBaseKind)c.Int();
                bg.color = c.Color(Color.white);
                bg.baseTexture = c.Texture();
                bg.normalKind = (BackgroundNormalKind)c.Int();
                bg.normalTexture = c.Texture();
            }
            int count = r.ReadInt32();
            if (count < 0 || count > 4096) throw new InvalidDataException("bad layer count");
            for (int i = 0; i < count; i++) d.layers.Add(ReadLayer(r));
            return d;
        }

        private static Layer ReadLayer(BinaryReader r)
        {
            var l = new Layer();
            using (var c = new ChunkReader(r))
            {
                l.id = c.String();
                l.name = c.String();
                l.visible = c.Bool(true);
                l.opacity = c.Float(1f);
                l.blendMode = (BlendMode)c.Int();

                if (c.More)
                {
                    using (var mc = new ChunkReader(c.R))
                    {
                        var m = l.mask;
                        m.source = (MaskSource)mc.Int();
                        m.selectedBits = mc.Ints();
                        m.paintRle = mc.Bytes();
                        m.image = mc.Texture();
                        m.embedded = mc.Bytes();
                        m.embeddedSize = mc.Int();
                        m.invert = mc.Bool();
                        m.opacity = mc.Float(1f);
                        m.feather = mc.Float();
                    }
                }

                int fillKind = c.Int();
                l.color = c.Color(Color.white);
                l.baseTexture = c.Texture();
                var tint = c.Color(Color.white);

                int normalKind = c.Int();
                l.normalTexture = c.Texture();
                l.normalScale = c.Float(1f);

                l.placementMode = (PlacementMode)c.Int();
                l.baseXform = c.Transform();
                l.syncNormal = c.Bool(true);
                l.normalXform = c.Transform();
                l.decalCenter = c.Vector2(new Vector2(0.5f, 0.5f));
                l.decalSize = c.Vector2(new Vector2(0.25f, 0.25f));
                l.decalRotation = c.Float();

                if (c.More)
                {
                    l.replaceNormal = c.Bool();
                }
                else
                {
                    // 旧形式: 種類の選択から今の形へ直す
                    l.legacyFillKind = fillKind;
                    l.legacyTint = tint;
                    l.legacyNormalKind = normalKind;
                    DocumentOps.MigrateLegacy(l);
                }
            }
            if (string.IsNullOrEmpty(l.id)) l.id = Layer.NewId();
            return l;
        }
    }

    /// <summary>0 / 255 のマスクラスターの圧縮（MaskSpec.embedded）。</summary>
    public static class MaskRaster
    {
        public static byte[] Compress(byte[] pixels)
        {
            using (var ms = new MemoryStream())
            {
                using (var d = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
                    d.Write(pixels, 0, pixels.Length);
                return ms.ToArray();
            }
        }

        /// <summary>size × size に展開する。壊れていたら null。</summary>
        public static byte[] Decompress(byte[] compressed, int size)
        {
            if (compressed == null || compressed.Length == 0 || size <= 0 || size > 16384) return null;
            try
            {
                var result = new byte[size * size];
                using (var ms = new MemoryStream(compressed))
                using (var d = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    int read = 0;
                    while (read < result.Length)
                    {
                        int n = d.Read(result, read, result.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return read == result.Length ? result : null;
                }
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>正方形のラスターを最近傍で縮小・拡大する。</summary>
        public static byte[] Resize(byte[] src, int srcSize, int dstSize)
        {
            if (srcSize == dstSize) return src;
            var dst = new byte[dstSize * dstSize];
            for (int y = 0; y < dstSize; y++)
            {
                int sy = Math.Min(srcSize - 1, (int)((y + 0.5f) * srcSize / dstSize));
                for (int x = 0; x < dstSize; x++)
                {
                    int sx = Math.Min(srcSize - 1, (int)((x + 0.5f) * srcSize / dstSize));
                    dst[y * dstSize + x] = src[sy * srcSize + sx];
                }
            }
            return dst;
        }
    }
}
