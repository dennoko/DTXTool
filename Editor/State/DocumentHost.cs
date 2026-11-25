using System;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// ドキュメント（レイヤー構成）の真実。Unity の Undo の対象。
    /// [FilePath] を付けないのでディスクには書かれない（保存は DocumentAutoSave と PSD 出力が行う）。
    /// ScriptableSingleton なのでドメインリロード後も中身と Undo 履歴が残る。
    /// 書き換えは必ず DocumentCommands を通す。
    /// </summary>
    internal sealed class DocumentHost : ScriptableSingleton<DocumentHost>
    {
        /// <summary>レイヤー一覧で「下地」を選んでいる時の SelectedLayerId（レイヤーの ID とは重ならない）。</summary>
        public const string BackgroundId = "@background";

        [SerializeField] private DtxDocument document = new DtxDocument();
        [SerializeField] private string targetKey = string.Empty;
        [SerializeField] private string selectedLayerId = string.Empty;

        /// <summary>変わるたびに +1（Undo / Redo を含む）。合成と UI はこれを見て作り直す。</summary>
        [NonSerialized] public int Revision;

        public static event Action Changed;

        public DtxDocument Doc => document ??= new DtxDocument();
        public string TargetKey => targetKey;
        public string SelectedLayerId => selectedLayerId;
        public Layer SelectedLayer => Doc.Find(selectedLayerId);

        private void OnEnable()
        {
            // 更新前のメモリ上のドキュメントは旧形式のまま復元されるので、ここで今の形へ直す
            DocumentOps.MigrateLegacy(Doc);
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        /// <summary>更新前の Undo 履歴を戻すと旧形式の値が入ってくるので、直してから通知する。</summary>
        private void OnUndoRedo()
        {
            DocumentOps.MigrateLegacy(Doc);
            EnsureSelectedValid();
            Touch();
        }

        // ── DocumentCommands からだけ呼ぶ ──

        internal void SetDocument(DtxDocument doc, string key)
        {
            document = doc ?? new DtxDocument();
            DocumentOps.MigrateLegacy(document);
            targetKey = key ?? string.Empty;
            EnsureSelectedValid();
        }

        internal void EnsureSelectedValid()
        {
            var doc = Doc;
            if (selectedLayerId != BackgroundId && doc.IndexOf(selectedLayerId) < 0)
                selectedLayerId = doc.layers.Count > 0 ? doc.layers[doc.layers.Count - 1].id : string.Empty;
        }

        internal void SetSelected(string layerId) => selectedLayerId = layerId ?? string.Empty;

        internal void Touch()
        {
            Revision++;
            Changed?.Invoke();
        }
    }
}
