using System;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// DocumentHost を変える唯一の入口。Undo の記録と変更通知は必ずここで行う。
    /// 中身の書き換えは Core の DocumentOps（Undo を知らない純粋な操作）に任せる。
    /// </summary>
    internal static class DocumentCommands
    {
        private static DocumentHost H => DocumentHost.instance;

        /// <summary>ドキュメントを書き換える。1 回の呼び出しが Undo 1 件になる。</summary>
        public static void Edit(string undoName, Action<DtxDocument> mutate)
        {
            Undo.RecordObject(H, undoName);
            mutate(H.Doc);
            H.Touch();
        }

        /// <summary>レイヤー 1 枚を書き換える。レイヤーが無ければ何もしない。</summary>
        public static void EditLayer(string layerId, string undoName, Action<Layer> mutate)
        {
            var layer = H.Doc.Find(layerId);
            if (layer == null) return;
            Undo.RecordObject(H, undoName);
            mutate(layer);
            H.Touch();
        }

        public static void EditSelected(string undoName, Action<Layer> mutate) => EditLayer(H.SelectedLayerId, undoName, mutate);

        public static void EditBackground(string undoName, Action<Background> mutate)
            => Edit(undoName, d => mutate(d.background));

        /// <summary>選択中のレイヤーを変える（Undo には積まないが、Undo で戻る内容には含まれる）。</summary>
        public static void Select(string layerId)
        {
            if (H.SelectedLayerId == (layerId ?? string.Empty)) return;
            H.SetSelected(layerId);
            H.Touch();
        }

        public static Layer AddSelectionLayer(string undoName, string name, int[] bits, byte[] paintRle)
        {
            Undo.RecordObject(H, undoName);
            var layer = DocumentOps.AddSelectionLayer(H.Doc, name, bits, paintRle);
            H.SetSelected(layer.id);
            H.Touch();
            return layer;
        }

        public static Layer AddImageMaskLayer(string undoName, string name, TextureRef image)
        {
            Undo.RecordObject(H, undoName);
            var layer = DocumentOps.AddImageMaskLayer(H.Doc, name, image);
            H.SetSelected(layer.id);
            H.Touch();
            return layer;
        }

        public static void Duplicate(string undoName, string layerId)
        {
            if (H.Doc.IndexOf(layerId) < 0) return;
            Undo.RecordObject(H, undoName);
            var copy = DocumentOps.Duplicate(H.Doc, layerId);
            if (copy != null) H.SetSelected(copy.id);
            H.Touch();
        }

        public static void Delete(string undoName, string layerId)
        {
            if (H.Doc.IndexOf(layerId) < 0) return;
            Undo.RecordObject(H, undoName);
            string next = DocumentOps.Delete(H.Doc, layerId);
            if (H.SelectedLayerId == layerId || H.Doc.IndexOf(H.SelectedLayerId) < 0) H.SetSelected(next);
            H.Touch();
        }

        public static void Move(string undoName, string layerId, int newIndex)
        {
            int i = H.Doc.IndexOf(layerId);
            if (i < 0 || i == Mathf.Clamp(newIndex, 0, H.Doc.layers.Count - 1)) return;
            Undo.RecordObject(H, undoName);
            DocumentOps.Move(H.Doc, layerId, newIndex);
            H.Touch();
        }

        /// <summary>ドキュメントごと差し替える（PSD の読み込み）。Ctrl+Z で読み込む前に戻せる。</summary>
        public static void Replace(string undoName, DtxDocument doc)
        {
            Undo.RecordObject(H, undoName);
            H.SetDocument(doc, H.TargetKey);
            H.Touch();
        }

        /// <summary>対象を切り替える。別の対象の履歴が混ざらないよう Undo を消してから入れ替える。</summary>
        public static void ResetForTarget(string targetKey, DtxDocument doc)
        {
            Undo.ClearUndo(H);
            H.SetDocument(doc, targetKey);
            H.Touch();
        }
    }
}
