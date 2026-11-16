using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dennoko.DTXTool.Core
{
    public enum PsdReadError
    {
        /// <summary>PSD ではない（先頭が "8BPS" でない、または PSB など対応外の形式）。</summary>
        NotPsd,
        /// <summary>PSD だが DTXTool の復元情報が入っていない。</summary>
        NoDtxData,
        /// <summary>途中で切れている・構造が壊れている。</summary>
        Corrupted,
    }

    public sealed class PsdReadException : Exception
    {
        public PsdReadError Error { get; }

        public PsdReadException(PsdReadError error, string message) : base(message)
        {
            Error = error;
        }
    }

    /// <summary>読み取ったレイヤー（検証・テスト用。ツールの復元には使わない）。</summary>
    public sealed class PsdLayerData
    {
        public string Name;
        public bool Visible;
        public byte Opacity;
        public string BlendKey;
        /// <summary>0 = 通常、1 / 2 = グループ（開 / 閉）、3 = グループの終端。</summary>
        public int SectionType;
        public int Top, Left, Bottom, Right;
        public bool HasMask;
        /// <summary>RGBA（行 0 = 上端）。画素を読まなかった時・グループは null。</summary>
        public byte[] Rgba;
        /// <summary>マスク（行 0 = 上端）。無ければ null。</summary>
        public byte[] Mask;
    }

    public sealed class PsdFileData
    {
        public int Width, Height, Channels, Depth, ColorMode;
        public readonly Dictionary<int, byte[]> Resources = new Dictionary<int, byte[]>();
        /// <summary>ファイル上の順（下から上）。</summary>
        public readonly List<PsdLayerData> Layers = new List<PsdLayerData>();
        /// <summary>統合画像 RGB（行 0 = 上端）。画素を読まなかった時は null。</summary>
        public byte[] MergedRgb;
    }

    /// <summary>
    /// 自前の PSD リーダー。ツールが使うのはヘッダーの確認と復元情報（Image Resource）の取り出しだけ。
    /// レイヤーの読み取りは、書き出した PSD が正しい構造かを確かめるために用意している。
    /// </summary>
    public static class PsdReader
    {
        /// <summary>DTXTool の復元情報を入れる Image Resource の ID（プラグイン用の 4000–4999 の範囲）。</summary>
        public const int DtxResourceId = 4090;
        public const string DtxResourceName = "DTXTool";

        /// <summary>DTXTool の復元情報を取り出す。無ければ PsdReadException。</summary>
        public static byte[] ReadDtxPayload(byte[] file)
        {
            var psd = Read(file, false, true);
            if (!psd.Resources.TryGetValue(DtxResourceId, out var payload) || !DocumentCodec.HasMagic(payload))
                throw new PsdReadException(PsdReadError.NoDtxData, "no DTXTool data in this PSD");
            return payload;
        }

        /// <summary>ファイルの先頭（ヘッダーと Image Resource）だけを読んで復元情報を取り出す。大きな PSD でも全体を読まない。</summary>
        public static byte[] ReadDtxPayload(Stream s)
        {
            using (var head = new MemoryStream())
            {
                if (!CopyExact(s, head, 26)) throw new PsdReadException(PsdReadError.NotPsd, "not a PSD file");
                var h = head.GetBuffer();
                if (h[0] != '8' || h[1] != 'B' || h[2] != 'P' || h[3] != 'S' || h[4] != 0 || h[5] != 1)
                    throw new PsdReadException(PsdReadError.NotPsd, "not a PSD file");
                for (int section = 0; section < 2; section++)   // カラーモードデータ、Image Resources
                {
                    var len = new byte[4];
                    if (s.Read(len, 0, 4) != 4) throw new PsdReadException(PsdReadError.Corrupted, "truncated");
                    head.Write(len, 0, 4);
                    long n = ((long)len[0] << 24) | ((long)len[1] << 16) | ((long)len[2] << 8) | len[3];
                    if (n > (1L << 30) || !CopyExact(s, head, n)) throw new PsdReadException(PsdReadError.Corrupted, "truncated");
                }
                return ReadDtxPayload(head.ToArray());
            }
        }

        private static bool CopyExact(Stream src, Stream dst, long count)
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (n <= 0) return false;
                dst.Write(buffer, 0, n);
                count -= n;
            }
            return true;
        }

        public static PsdFileData Read(byte[] d, bool decodePixels, bool resourcesOnly = false)
        {
            if (d == null || d.Length < 26 || d[0] != '8' || d[1] != 'B' || d[2] != 'P' || d[3] != 'S')
                throw new PsdReadException(PsdReadError.NotPsd, "not a PSD file");
            try
            {
                return ReadCore(d, decodePixels, resourcesOnly);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException)
            {
                throw new PsdReadException(PsdReadError.Corrupted, "broken PSD: " + e.Message);
            }
        }

        private static PsdFileData ReadCore(byte[] d, bool decodePixels, bool resourcesOnly)
        {
            int p = 4;
            int version = BigEndian.ReadU16(d, ref p);
            if (version != 1) throw new PsdReadException(PsdReadError.NotPsd, "unsupported PSD version " + version);
            p += 6;
            var psd = new PsdFileData();
            psd.Channels = BigEndian.ReadU16(d, ref p);
            psd.Height = BigEndian.ReadI32(d, ref p);
            psd.Width = BigEndian.ReadI32(d, ref p);
            psd.Depth = BigEndian.ReadU16(d, ref p);
            psd.ColorMode = BigEndian.ReadU16(d, ref p);
            if (psd.Width < 1 || psd.Height < 1 || psd.Width > 30000 || psd.Height > 30000)
                throw new PsdReadException(PsdReadError.Corrupted, "bad size");

            // カラーモードデータ
            long colorLen = BigEndian.ReadU32(d, ref p);
            p = Advance(d, p, colorLen);

            // Image Resources
            long resLen = BigEndian.ReadU32(d, ref p);
            int resEnd = Advance(d, p, resLen);
            while (p + 12 <= resEnd)
            {
                string sig = BigEndian.ReadAscii(d, ref p, 4);
                if (sig != "8BIM" && sig != "MeSa") throw new PsdReadException(PsdReadError.Corrupted, "bad resource signature");
                int id = BigEndian.ReadU16(d, ref p);
                int nameLen = d[p];
                p += 1 + nameLen;
                if (((1 + nameLen) & 1) != 0) p++;
                long size = BigEndian.ReadU32(d, ref p);
                int dataEnd = Advance(d, p, size);
                if (dataEnd > resEnd) throw new PsdReadException(PsdReadError.Corrupted, "resource overruns section");
                var data = new byte[size];
                Buffer.BlockCopy(d, p, data, 0, (int)size);
                psd.Resources[id] = data;
                p = dataEnd + (int)(size & 1);
            }
            p = resEnd;
            if (resourcesOnly) return psd;

            // レイヤーとマスクの情報
            long sectionLen = BigEndian.ReadU32(d, ref p);
            int sectionEnd = Advance(d, p, sectionLen);
            if (sectionLen > 0)
            {
                long layerInfoLen = BigEndian.ReadU32(d, ref p);
                int layerInfoEnd = Advance(d, p, layerInfoLen);
                if (layerInfoLen > 0) ReadLayers(d, ref p, psd, decodePixels);
                if (p > layerInfoEnd) throw new PsdReadException(PsdReadError.Corrupted, "layer info overruns section");
            }
            p = sectionEnd;

            if (decodePixels) psd.MergedRgb = ReadMerged(d, p, psd);
            return psd;
        }

        private static int Advance(byte[] d, int p, long length)
        {
            long end = p + length;
            if (length < 0 || end > d.Length) throw new PsdReadException(PsdReadError.Corrupted, "section overruns file");
            return (int)end;
        }

        private static void ReadLayers(byte[] d, ref int p, PsdFileData psd, bool decodePixels)
        {
            int count = Math.Abs(BigEndian.ReadI16(d, ref p));
            var channelInfo = new List<List<(int id, long length)>>(count);
            for (int i = 0; i < count; i++)
            {
                var layer = new PsdLayerData();
                layer.Top = BigEndian.ReadI32(d, ref p);
                layer.Left = BigEndian.ReadI32(d, ref p);
                layer.Bottom = BigEndian.ReadI32(d, ref p);
                layer.Right = BigEndian.ReadI32(d, ref p);
                int channels = BigEndian.ReadU16(d, ref p);
                var info = new List<(int, long)>(channels);
                for (int c = 0; c < channels; c++)
                {
                    int id = BigEndian.ReadI16(d, ref p);
                    long len = BigEndian.ReadU32(d, ref p);
                    info.Add((id, len));
                }
                channelInfo.Add(info);

                string sig = BigEndian.ReadAscii(d, ref p, 4);
                if (sig != "8BIM") throw new PsdReadException(PsdReadError.Corrupted, "bad blend signature");
                layer.BlendKey = BigEndian.ReadAscii(d, ref p, 4);
                layer.Opacity = d[p++];
                p++;                                  // クリッピング
                int flags = d[p++];
                layer.Visible = (flags & 0x02) == 0;
                p++;                                  // 詰め物

                long extraLen = BigEndian.ReadU32(d, ref p);
                int extraEnd = Advance(d, p, extraLen);

                long maskLen = BigEndian.ReadU32(d, ref p);
                layer.HasMask = maskLen > 0;
                p = Advance(d, p, maskLen);
                long rangesLen = BigEndian.ReadU32(d, ref p);
                p = Advance(d, p, rangesLen);

                int nameLen = d[p];
                layer.Name = Encoding.ASCII.GetString(d, p + 1, nameLen);
                p += 1 + nameLen;
                p += (4 - ((1 + nameLen) & 3)) & 3;

                while (p + 12 <= extraEnd)
                {
                    string bsig = BigEndian.ReadAscii(d, ref p, 4);
                    if (bsig != "8BIM" && bsig != "8B64") throw new PsdReadException(PsdReadError.Corrupted, "bad layer info signature");
                    string key = BigEndian.ReadAscii(d, ref p, 4);
                    long len = BigEndian.ReadU32(d, ref p);
                    int blockEnd = Advance(d, p, len);
                    if (key == "luni")
                    {
                        int q = p;
                        long chars = BigEndian.ReadU32(d, ref q);
                        var sb = new StringBuilder((int)chars);
                        for (int k = 0; k < chars; k++) sb.Append((char)BigEndian.ReadU16(d, ref q));
                        layer.Name = sb.ToString().TrimEnd('\0');
                    }
                    else if (key == "lsct")
                    {
                        int q = p;
                        layer.SectionType = BigEndian.ReadI32(d, ref q);
                    }
                    p = blockEnd + (int)(len & 1);
                }
                p = extraEnd;
                psd.Layers.Add(layer);
            }

            // チャンネルの画素（レイヤーの順）
            for (int i = 0; i < count; i++)
            {
                var layer = psd.Layers[i];
                int w = layer.Right - layer.Left, h = layer.Bottom - layer.Top;
                foreach (var (id, len) in channelInfo[i])
                {
                    int end = Advance(d, p, len);
                    if (decodePixels && w > 0 && h > 0 && w == psd.Width && h == psd.Height)
                    {
                        var plane = DecodeChannel(d, p, end, w, h);
                        if (id == -2) layer.Mask = plane;
                        else
                        {
                            layer.Rgba ??= new byte[w * h * 4];
                            int offset = id == -1 ? 3 : id;
                            if (offset >= 0 && offset <= 3)
                                for (int k = 0; k < plane.Length; k++) layer.Rgba[k * 4 + offset] = plane[k];
                        }
                    }
                    p = end;
                }
            }
        }

        private static byte[] DecodeChannel(byte[] d, int p, int end, int w, int h)
        {
            var plane = new byte[w * h];
            int compression = BigEndian.ReadU16(d, ref p);
            if (compression == 0)
            {
                if (end - p < plane.Length) throw new PsdReadException(PsdReadError.Corrupted, "raw channel too short");
                Buffer.BlockCopy(d, p, plane, 0, plane.Length);
                return plane;
            }
            if (compression != 1) throw new PsdReadException(PsdReadError.Corrupted, "unsupported compression " + compression);
            var counts = new int[h];
            for (int y = 0; y < h; y++) counts[y] = BigEndian.ReadU16(d, ref p);
            for (int y = 0; y < h; y++)
            {
                if (p + counts[y] > end || !PackBits.DecodeRow(d, ref p, counts[y], plane, y * w, w))
                    throw new PsdReadException(PsdReadError.Corrupted, "broken RLE row");
            }
            return plane;
        }

        private static byte[] ReadMerged(byte[] d, int p, PsdFileData psd)
        {
            int w = psd.Width, h = psd.Height;
            int channels = Math.Min(psd.Channels, 3);
            var rgb = new byte[w * h * 3];
            int compression = BigEndian.ReadU16(d, ref p);
            if (compression == 0)
            {
                for (int c = 0; c < channels; c++)
                    for (int k = 0; k < w * h; k++) rgb[k * 3 + c] = d[p + c * w * h + k];
                return rgb;
            }
            if (compression != 1) throw new PsdReadException(PsdReadError.Corrupted, "unsupported compression " + compression);
            var counts = new int[psd.Channels * h];
            for (int i = 0; i < counts.Length; i++) counts[i] = BigEndian.ReadU16(d, ref p);
            var row = new byte[w];
            for (int c = 0; c < psd.Channels; c++)
            {
                for (int y = 0; y < h; y++)
                {
                    int n = counts[c * h + y];
                    if (p + n > d.Length || !PackBits.DecodeRow(d, ref p, n, row, 0, w))
                        throw new PsdReadException(PsdReadError.Corrupted, "broken merged RLE row");
                    if (c < 3)
                        for (int x = 0; x < w; x++) rgb[(y * w + x) * 3 + c] = row[x];
                }
            }
            return rgb;
        }
    }
}
