using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dennoko.DTXTool.Core
{
    /// <summary>PSD の数値はすべてビッグエンディアン。</summary>
    internal static class BigEndian
    {
        public static void U16(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        public static void U32(Stream s, long v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        public static void Ascii(Stream s, string text)
        {
            for (int i = 0; i < text.Length; i++) s.WriteByte((byte)text[i]);
        }

        public static int ReadU16(byte[] d, ref int p)
        {
            int v = (d[p] << 8) | d[p + 1];
            p += 2;
            return v;
        }

        public static int ReadI16(byte[] d, ref int p) => (short)ReadU16(d, ref p);

        public static long ReadU32(byte[] d, ref int p)
        {
            long v = ((long)d[p] << 24) | ((long)d[p + 1] << 16) | ((long)d[p + 2] << 8) | d[p + 3];
            p += 4;
            return v;
        }

        public static int ReadI32(byte[] d, ref int p) => (int)ReadU32(d, ref p);

        public static string ReadAscii(byte[] d, ref int p, int n)
        {
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append((char)d[p + i]);
            p += n;
            return sb.ToString();
        }
    }

    /// <summary>PackBits（PSD の RLE 圧縮）。</summary>
    public static class PackBits
    {
        /// <summary>1 行を圧縮して dst に足す。stride は入力の画素間隔（RGBA の 1 チャンネルなら 4）。戻り値は書いたバイト数。</summary>
        public static int EncodeRow(byte[] src, int offset, int count, int stride, Stream dst)
        {
            int written = 0;
            int i = 0;
            while (i < count)
            {
                // 同じ値の並び
                int run = 1;
                byte v = src[offset + i * stride];
                while (i + run < count && run < 128 && src[offset + (i + run) * stride] == v) run++;
                // 2 個だけの並びは違う値の並びに含める（そのほうが短い）
                if (run >= 3)
                {
                    dst.WriteByte((byte)(257 - run));
                    dst.WriteByte(v);
                    written += 2;
                    i += run;
                    continue;
                }

                // 違う値の並び（次に 3 個以上の同じ値が来る手前まで）
                run = 0;
                int start = i;
                int lit = 0;
                while (i < count && lit < 128)
                {
                    if (i + 2 < count)
                    {
                        byte a = src[offset + i * stride];
                        if (a == src[offset + (i + 1) * stride] && a == src[offset + (i + 2) * stride]) break;
                    }
                    i++;
                    lit++;
                }
                dst.WriteByte((byte)(lit - 1));
                for (int k = 0; k < lit; k++) dst.WriteByte(src[offset + (start + k) * stride]);
                written += 1 + lit;
            }
            return written;
        }

        /// <summary>圧縮された 1 行を展開する。壊れていたら false。</summary>
        public static bool DecodeRow(byte[] src, ref int pos, int packedLength, byte[] dst, int dstOffset, int count)
        {
            int end = pos + packedLength;
            int o = 0;
            while (pos < end && o < count)
            {
                sbyte n = (sbyte)src[pos++];
                if (n >= 0)
                {
                    int len = n + 1;
                    if (pos + len > end || o + len > count) return false;
                    Buffer.BlockCopy(src, pos, dst, dstOffset + o, len);
                    pos += len;
                    o += len;
                }
                else if (n != -128)
                {
                    int len = 1 - n;
                    if (pos >= end || o + len > count) return false;
                    byte v = src[pos++];
                    for (int k = 0; k < len; k++) dst[dstOffset + o + k] = v;
                    o += len;
                }
            }
            pos = end;
            return o == count;
        }
    }

    /// <summary>
    /// 自前の PSD ライター（8bit RGB / RLE 圧縮 / PSD バージョン 1）。
    /// レイヤーは下から上の順に足す。グループは BeginGroup → 中身 → EndGroup。
    /// 画素は足した時点で圧縮して scratch に貯めるので、非圧縮の全レイヤーをメモリに持たない。
    /// </summary>
    public sealed class PsdWriter : IDisposable
    {
        public const string BlendNormal = "norm";
        public const string BlendMultiply = "mul ";
        public const string BlendLinearDodge = "lddg";
        public const string BlendScreen = "scrn";
        public const string BlendOverlay = "over";
        public const string BlendPassThrough = "pass";

        private sealed class Record
        {
            public string Name;
            public bool Visible;
            public byte Opacity;
            public string Blend;
            public int SectionType;        // 0 = 通常、1 = 開いたグループ、2 = 閉じたグループ、3 = グループの終端
            public bool HasMask;
            public readonly List<(int id, long length)> Channels = new List<(int, long)>();
        }

        private readonly int _width, _height;
        private readonly Stream _scratch;
        private readonly bool _ownsScratch;
        private readonly List<Record> _records = new List<Record>();
        private readonly List<(int id, string name, byte[] data)> _resources = new List<(int, string, byte[])>();
        private int _openGroups;
        private bool _written;

        /// <param name="scratch">圧縮済みの画素を貯める場所（大きい出力では一時ファイル）。null ならメモリ。</param>
        public PsdWriter(int width, int height, Stream scratch = null)
        {
            if (width < 1 || height < 1 || width > 30000 || height > 30000)
                throw new ArgumentException("PSD size must be 1..30000: " + width + "x" + height);
            _width = width;
            _height = height;
            _scratch = scratch ?? new MemoryStream();
            _ownsScratch = scratch == null;
        }

        public static string BlendKey(BlendMode mode)
        {
            switch (mode)
            {
                case BlendMode.Multiply: return BlendMultiply;
                case BlendMode.Additive: return BlendLinearDodge;
                case BlendMode.Screen: return BlendScreen;
                case BlendMode.Overlay: return BlendOverlay;
                default: return BlendNormal;
            }
        }

        public static bool TryParseBlendKey(string key, out BlendMode mode)
        {
            switch (key)
            {
                case BlendNormal: mode = BlendMode.Normal; return true;
                case BlendMultiply: mode = BlendMode.Multiply; return true;
                case BlendLinearDodge: mode = BlendMode.Additive; return true;
                case BlendScreen: mode = BlendMode.Screen; return true;
                case BlendOverlay: mode = BlendMode.Overlay; return true;
                default: mode = BlendMode.Normal; return false;
            }
        }

        /// <summary>Image Resource ブロックを足す（DTXTool の復元情報など）。</summary>
        public void AddImageResource(int id, string name, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            _resources.Add((id, name ?? string.Empty, data));
        }

        /// <summary>グループの始まり（ファイル上は終端の印）。</summary>
        public void BeginGroup()
        {
            var r = new Record { Name = "</Layer group>", Visible = true, Opacity = 255, Blend = BlendNormal, SectionType = 3 };
            AddEmptyChannels(r);
            _records.Add(r);
            _openGroups++;
        }

        /// <summary>グループの終わり（ファイル上はフォルダそのもの）。</summary>
        public void EndGroup(string name, bool visible, bool expanded = true)
        {
            if (_openGroups <= 0) throw new InvalidOperationException("EndGroup without BeginGroup");
            _openGroups--;
            var r = new Record { Name = name, Visible = visible, Opacity = 255, Blend = BlendPassThrough, SectionType = expanded ? 1 : 2 };
            AddEmptyChannels(r);
            _records.Add(r);
        }

        /// <summary>
        /// 全面のレイヤーを足す。rgba は幅×高さ×4（ストレートアルファ）、mask は幅×高さ（null ならマスクなし）。
        /// bottomUp = true なら配列の行 0 が画像の下端（Unity の読み戻しの向き）。
        /// </summary>
        public void AddLayer(string name, bool visible, float opacity, string blendKey, byte[] rgba, byte[] mask, bool bottomUp)
        {
            long pixels = (long)_width * _height;
            if (rgba == null || rgba.Length != pixels * 4) throw new ArgumentException("rgba size mismatch: " + name);
            if (mask != null && mask.Length != pixels) throw new ArgumentException("mask size mismatch: " + name);
            if (blendKey == null || blendKey.Length != 4) throw new ArgumentException("blend key must be 4 chars");

            var r = new Record
            {
                Name = name,
                Visible = visible,
                Opacity = (byte)Math.Round(Math.Max(0f, Math.Min(1f, opacity)) * 255f),
                Blend = blendKey,
                SectionType = 0,
                HasMask = mask != null,
            };
            r.Channels.Add((-1, WriteChannel(rgba, 3, 4, bottomUp)));
            r.Channels.Add((0, WriteChannel(rgba, 0, 4, bottomUp)));
            r.Channels.Add((1, WriteChannel(rgba, 1, 4, bottomUp)));
            r.Channels.Add((2, WriteChannel(rgba, 2, 4, bottomUp)));
            if (mask != null) r.Channels.Add((-2, WriteChannel(mask, 0, 1, bottomUp)));
            _records.Add(r);
        }

        private void AddEmptyChannels(Record r)
        {
            foreach (int id in new[] { -1, 0, 1, 2 })
            {
                BigEndian.U16(_scratch, 0);   // 圧縮なし・データなし
                r.Channels.Add((id, 2));
            }
        }

        /// <summary>1 チャンネルを RLE で scratch に書く。戻り値は圧縮方式の 2 バイトを含む長さ。</summary>
        private long WriteChannel(byte[] src, int channelOffset, int stride, bool bottomUp)
        {
            long start = _scratch.Position;
            BigEndian.U16(_scratch, 1);
            long countsPos = _scratch.Position;
            for (int y = 0; y < _height; y++) BigEndian.U16(_scratch, 0);

            var counts = new int[_height];
            for (int y = 0; y < _height; y++)
            {
                int srcRow = bottomUp ? _height - 1 - y : y;
                counts[y] = PackBits.EncodeRow(src, srcRow * _width * stride + channelOffset, _width, stride, _scratch);
            }
            long end = _scratch.Position;
            _scratch.Position = countsPos;
            for (int y = 0; y < _height; y++) BigEndian.U16(_scratch, counts[y]);
            _scratch.Position = end;
            return end - start;
        }

        /// <summary>
        /// ファイルを書き出す。mergedRgba は統合画像（幅×高さ×4。RGB だけ使う）。
        /// </summary>
        public void Write(Stream output, byte[] mergedRgba, bool bottomUp)
        {
            if (_written) throw new InvalidOperationException("already written");
            if (_openGroups != 0) throw new InvalidOperationException("unclosed group");
            if (mergedRgba == null || mergedRgba.Length != (long)_width * _height * 4) throw new ArgumentException("merged image size mismatch");
            _written = true;

            // ヘッダー
            BigEndian.Ascii(output, "8BPS");
            BigEndian.U16(output, 1);
            for (int i = 0; i < 6; i++) output.WriteByte(0);
            BigEndian.U16(output, 3);            // 統合画像のチャンネル数（RGB）
            BigEndian.U32(output, _height);
            BigEndian.U32(output, _width);
            BigEndian.U16(output, 8);
            BigEndian.U16(output, 3);            // RGB

            // カラーモードデータ（なし）
            BigEndian.U32(output, 0);

            // Image Resources
            using (var res = new MemoryStream())
            {
                foreach (var (id, name, data) in _resources) WriteResource(res, id, name, data);
                BigEndian.U32(output, res.Length);
                res.Position = 0;
                res.CopyTo(output);
            }

            WriteLayerAndMask(output);
            WriteMerged(output, mergedRgba, bottomUp);
        }

        private static void WriteResource(Stream s, int id, string name, byte[] data)
        {
            BigEndian.Ascii(s, "8BIM");
            BigEndian.U16(s, id);
            var n = AsciiName(name, 255);
            s.WriteByte((byte)n.Length);
            s.Write(n, 0, n.Length);
            if (((1 + n.Length) & 1) != 0) s.WriteByte(0);
            BigEndian.U32(s, data.Length);
            s.Write(data, 0, data.Length);
            if ((data.Length & 1) != 0) s.WriteByte(0);
        }

        private void WriteLayerAndMask(Stream output)
        {
            byte[] recordBytes;
            using (var ms = new MemoryStream())
            {
                foreach (var r in _records) WriteRecord(ms, r);
                recordBytes = ms.ToArray();
            }

            long layerInfo = 2 + recordBytes.Length + _scratch.Length;
            long pad = layerInfo & 1;
            long layerInfoPadded = layerInfo + pad;
            long section = 4 + layerInfoPadded + 4;   // レイヤー情報の長さ + 本体 + グローバルマスク情報の長さ
            if (section > uint.MaxValue)
                throw new InvalidOperationException("PSD layer data exceeds 4 GB. Lower the resolution or the number of layers.");

            BigEndian.U32(output, section);
            BigEndian.U32(output, layerInfoPadded);
            BigEndian.U16(output, _records.Count);
            output.Write(recordBytes, 0, recordBytes.Length);
            _scratch.Flush();
            _scratch.Position = 0;
            _scratch.CopyTo(output);
            if (pad != 0) output.WriteByte(0);
            BigEndian.U32(output, 0);                 // グローバルレイヤーマスク情報（なし）
        }

        private void WriteRecord(Stream s, Record r)
        {
            bool pixels = r.SectionType == 0;
            BigEndian.U32(s, 0);
            BigEndian.U32(s, 0);
            BigEndian.U32(s, pixels ? _height : 0);
            BigEndian.U32(s, pixels ? _width : 0);

            BigEndian.U16(s, r.Channels.Count);
            foreach (var (id, length) in r.Channels)
            {
                BigEndian.U16(s, id & 0xFFFF);
                BigEndian.U32(s, length);
            }

            BigEndian.Ascii(s, "8BIM");
            BigEndian.Ascii(s, r.Blend);
            s.WriteByte(r.Opacity);
            s.WriteByte(0);                           // クリッピング: base
            int flags = 0x08;                         // bit4 の情報が有効
            if (!r.Visible) flags |= 0x02;
            if (!pixels) flags |= 0x10;               // 画素が見た目に関係しない（グループ）
            s.WriteByte((byte)flags);
            s.WriteByte(0);

            using (var extra = new MemoryStream())
            {
                // レイヤーマスク
                if (r.HasMask)
                {
                    BigEndian.U32(extra, 20);
                    BigEndian.U32(extra, 0);
                    BigEndian.U32(extra, 0);
                    BigEndian.U32(extra, _height);
                    BigEndian.U32(extra, _width);
                    extra.WriteByte(0);               // マスクの外側の値
                    extra.WriteByte(0);               // フラグ
                    extra.WriteByte(0);
                    extra.WriteByte(0);
                }
                else
                {
                    BigEndian.U32(extra, 0);
                }

                // ブレンド範囲（なし）
                BigEndian.U32(extra, 0);

                // 名前（Pascal 文字列。4 の倍数に詰める）。日本語は下の luni が使われる
                var n = AsciiName(r.Name, 255);
                extra.WriteByte((byte)n.Length);
                extra.Write(n, 0, n.Length);
                int padName = (4 - ((1 + n.Length) & 3)) & 3;
                for (int i = 0; i < padName; i++) extra.WriteByte(0);

                // Unicode の名前
                string name = r.Name ?? string.Empty;
                BigEndian.Ascii(extra, "8BIM");
                BigEndian.Ascii(extra, "luni");
                int luniLength = 4 + name.Length * 2;
                BigEndian.U32(extra, luniLength + (luniLength & 1));
                BigEndian.U32(extra, name.Length);
                foreach (char c in name) BigEndian.U16(extra, c);
                if ((luniLength & 1) != 0) extra.WriteByte(0);

                // グループの区切り
                if (r.SectionType != 0)
                {
                    BigEndian.Ascii(extra, "8BIM");
                    BigEndian.Ascii(extra, "lsct");
                    BigEndian.U32(extra, 12);
                    BigEndian.U32(extra, r.SectionType);
                    BigEndian.Ascii(extra, "8BIM");
                    BigEndian.Ascii(extra, r.SectionType == 3 ? BlendNormal : r.Blend);
                }

                BigEndian.U32(s, extra.Length);
                extra.Position = 0;
                extra.CopyTo(s);
            }
        }

        private void WriteMerged(Stream output, byte[] rgba, bool bottomUp)
        {
            BigEndian.U16(output, 1);
            using (var data = new MemoryStream())
            {
                var counts = new int[3 * _height];
                for (int c = 0; c < 3; c++)
                {
                    for (int y = 0; y < _height; y++)
                    {
                        int srcRow = bottomUp ? _height - 1 - y : y;
                        counts[c * _height + y] = PackBits.EncodeRow(rgba, srcRow * _width * 4 + c, _width, 4, data);
                    }
                }
                foreach (int n in counts) BigEndian.U16(output, n);
                data.Position = 0;
                data.CopyTo(output);
            }
        }

        private static byte[] AsciiName(string name, int max)
        {
            name ??= string.Empty;
            int n = Math.Min(name.Length, max);
            var bytes = new byte[n];
            for (int i = 0; i < n; i++)
            {
                char c = name[i];
                bytes[i] = c >= 0x20 && c < 0x7F ? (byte)c : (byte)'_';
            }
            return bytes;
        }

        public void Dispose()
        {
            if (_ownsScratch) _scratch.Dispose();
        }
    }
}
