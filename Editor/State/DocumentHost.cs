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
            Undo.undoRedoPerformed += Touch;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Touch;
        }

        // ── DocumentCommands からだけ呼ぶ ──

        internal void SetDocument(DtxDocument doc, string key)
        {
            document = doc ?? new DtxDocument();
            targetKey = key ?? string.Empty;
            if (selectedLayerId != BackgroundId && document.IndexOf(selectedLayerId) < 0)
                selectedLayerId = document.layers.Count > 0 ? document.layers[document.layers.Count - 1].id : string.Empty;
        }

        internal void SetSelected(string layerId) => selectedLayerId = layerId ?? string.Empty;

        internal void Touch()
        {
            Revision++;
            Changed?.Invoke();
        }
    }
}
