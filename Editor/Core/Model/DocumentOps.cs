using System;
using UnityEngine;

namespace Dennoko.DTXTool.Core
{
    /// <summary>
    /// ドキュメントの書き換え（Undo や通知を含まない純粋な操作）。
    /// エディター側の DocumentCommands が Undo の記録と変更通知を付けて呼ぶ。
    /// </summary>
    public static class DocumentOps
    {
        /// <summary>選択範囲から新しいレイヤーを作る（最上段に足す）。</summary>
        public static Layer AddSelectionLayer(DtxDocument doc, string name, int[] selectedBits, byte[] paintRle)
        {
            var layer = new Layer
            {
                id = Layer.NewId(),
                name = doc.UniqueName(name),
            };
            layer.mask.source = MaskSource.Selection;
            layer.mask.selectedBits = selectedBits != null ? (int[])selectedBits.Clone() : Array.Empty<int>();
            layer.mask.paintRle = paintRle != null ? (byte[])paintRle.Clone() : Array.Empty<byte>();
            doc.layers.Add(layer);
            return layer;
        }

        /// <summary>マスク画像から新しいレイヤーを作る（最上段に足す）。</summary>
        public static Layer AddImageMaskLayer(DtxDocument doc, string name, TextureRef image)
        {
            var layer = new Layer
            {
                id = Layer.NewId(),
                name = doc.UniqueName(name),
            };
            layer.mask.source = MaskSource.Image;
            layer.mask.image = image != null ? image.Clone() : new TextureRef();
            doc.layers.Add(layer);
            return layer;
        }

        /// <summary>複製して元のすぐ上に入れる。</summary>
        public static Layer Duplicate(DtxDocument doc, string layerId)
        {
            int i = doc.IndexOf(layerId);
            if (i < 0) return null;
            var copy = DocumentCodec.CloneLayer(doc.layers[i]);
            copy.id = Layer.NewId();
            copy.name = doc.UniqueName(doc.layers[i].name);
            doc.layers.Insert(i + 1, copy);
            return copy;
        }

        /// <summary>削除する。戻り値は次に選ぶレイヤーの ID（無ければ空）。</summary>
        public static string Delete(DtxDocument doc, string layerId)
        {
            int i = doc.IndexOf(layerId);
            if (i < 0) return string.Empty;
            doc.layers.RemoveAt(i);
            if (doc.layers.Count == 0) return string.Empty;
            return doc.layers[Math.Min(i, doc.layers.Count - 1)].id;
        }

        /// <summary>newIndex（0 = 最下層）へ動かす。動いたら true。</summary>
        public static bool Move(DtxDocument doc, string layerId, int newIndex)
        {
            int i = doc.IndexOf(layerId);
            if (i < 0) return false;
            newIndex = Math.Max(0, Math.Min(doc.layers.Count - 1, newIndex));
            if (newIndex == i) return false;
            var l = doc.layers[i];
            doc.layers.RemoveAt(i);
            doc.layers.Insert(newIndex, l);
            return true;
        }

        /// <summary>
        /// 対象メッシュに合わせる。選択三角形はメッシュが同じ時だけ使えるので、違っていたら
        /// 埋め込みラスターがあればそれに切り替え、無ければ空の選択にする。戻り値は選択を復元できなかったレイヤーの数。
        /// </summary>
        public static int AdaptToMesh(DtxDocument doc, long meshHash, int triangleCount)
        {
            bool same = doc.meshHash == meshHash && doc.triangleCount == triangleCount;
            int words = TriangleBits.WordCount(triangleCount);
            int lost = 0;
            foreach (var l in doc.layers)
            {
                var m = l.mask;
                if (m.source != MaskSource.Selection) continue;
                if (same && m.selectedBits != null && m.selectedBits.Length == words)
                {
                    // 選択が使えるので控えのラスターは要らない
                    m.embedded = Array.Empty<byte>();
                    m.embeddedSize = 0;
                    continue;
                }
                lost++;
                m.selectedBits = new int[words];
                m.paintRle = Array.Empty<byte>();
                if (m.HasEmbedded) m.source = MaskSource.Embedded;
            }
            doc.meshHash = meshHash;
            doc.triangleCount = triangleCount;
            return lost;
        }

        /// <summary>出力解像度に最も近い選択肢（512 / 1024 / 2048 / 4096）。</summary>
        public static int NearestSize(int pixels)
        {
            int best = DtxDocument.Sizes[0];
            foreach (int s in DtxDocument.Sizes)
                if (Math.Abs(s - pixels) < Math.Abs(best - pixels)) best = s;
            return best;
        }

        public static void SetSize(DtxDocument doc, int size)
        {
            size = Mathf.Clamp(size, 16, 8192);
            doc.width = size;
            doc.height = size;
        }

        /// <summary>レイヤーが合成に寄与するか（非表示・不透明度 0 は飛ばす）。</summary>
        public static bool Contributes(Layer l) => l != null && l.visible && l.opacity > 0f && l.mask.opacity > 0f;

        /// <summary>ぼかしの半径（px）。</summary>
        public static int FeatherPixels(MaskSpec m, int size) => Mathf.Clamp(Mathf.RoundToInt(m.feather * size), 0, 256);
    }
}
