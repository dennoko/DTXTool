using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// 編集モードの切り替え（Docs/Impl/ARCHITECTURE.md §4.4）。
    ///   LayerEdit : レイヤーの調整。シーンのクリックは奪わない
    ///   SelectNew : 作業中の選択（EditState）に範囲を選ぶ → Confirm で新しいレイヤーになる
    ///   Reselect  : 既存レイヤーの範囲を EditState に読み込んで直す → Confirm でレイヤーへ書き戻す
    /// モードは Session 層の設定に持つので、ドメインリロードをまたいで続けられる。
    /// 範囲の再調整中は、合成が作業中の選択のマスクを直接使うので、確定前でもモデル上の見た目がその場で変わる。
    /// </summary>
    internal static class DtxModeController
    {
        public static DtxMode Mode => SettingsService.Get(SettingKeys.EditMode);

        public static string EditingLayerId => SettingsService.Get(SettingKeys.EditingLayerId);

        /// <summary>範囲を選んでいる最中（シーンと UV ビューのクリックを選択に使う）。</summary>
        public static bool IsSelecting => Mode != DtxMode.LayerEdit;

        /// <summary>新しいレイヤーの範囲選択を始める。</summary>
        public static void BeginSelectNew()
        {
            var s = SessionController.Current;
            if (s == null) return;
            s.Paint?.DiscardStroke();
            EditCommands.Restore(L10n.T("undo.beginSelect"),
                TriangleBits.Create(s.Analysis.TriangleCount), PaintRle.Empty(EditState.PaintSize * EditState.PaintSize));
            Enter(DtxMode.SelectNew, string.Empty);
        }

        /// <summary>既存レイヤーの範囲の再調整を始める。選択で作ったレイヤーでなければ、空の選択から作り直すことになる。</summary>
        public static void BeginReselect(string layerId)
        {
            var s = SessionController.Current;
            var layer = DocumentHost.instance.Doc.Find(layerId);
            if (s == null || layer == null) return;
            s.Paint?.DiscardStroke();

            int words = TriangleBits.WordCount(s.Analysis.TriangleCount);
            var m = layer.mask;
            bool usable = m.source == MaskSource.Selection && m.selectedBits != null && m.selectedBits.Length == words;
            int[] bits = usable ? TriangleBits.Copy(m.selectedBits) : TriangleBits.Create(s.Analysis.TriangleCount);
            byte[] paint = usable && !PaintRle.IsEmpty(m.paintRle)
                ? (byte[])m.paintRle.Clone()
                : PaintRle.Empty(EditState.PaintSize * EditState.PaintSize);
            EditCommands.Restore(L10n.T("undo.beginSelect"), bits, paint);
            DocumentCommands.Select(layerId);
            Enter(DtxMode.Reselect, layerId);
        }

        /// <summary>選択を確定する。SelectNew は新しいレイヤーを作り、Reselect はレイヤーの範囲を書き換える。</summary>
        public static bool Confirm(MessageOrigin origin = MessageOrigin.Window)
        {
            var s = SessionController.Current;
            if (s == null || !IsSelecting) return false;
            CommitPendingStroke(s);
            var es = EditState.instance;
            bool empty = !TriangleBits.AnySet(es.SelectedBits) && PaintRle.IsEmpty(es.PaintRle);

            if (Mode == DtxMode.SelectNew)
            {
                if (empty)
                {
                    Notifier.Post(MessageLevel.Warning, L10n.T("status.selectionEmpty"), origin);
                    return false;
                }
                var layer = DocumentCommands.AddSelectionLayer(L10n.T("undo.addLayer"), L10n.T("layer.defaultName"), es.SelectedBits, es.PaintRle);
                // 新しいレイヤーは見分けやすい色で始める（すぐに質感を選び直す前提）
                layer.color = NextLayerColor(DocumentHost.instance.Doc.layers.Count);
                Leave();
                Notifier.Post(MessageLevel.Success, L10n.F("status.layerAdded", layer.name), origin);
                return true;
            }

            string id = EditingLayerId;
            int[] bits = TriangleBits.Copy(es.SelectedBits);
            byte[] paint = (byte[])es.PaintRle.Clone();
            DocumentCommands.EditLayer(id, L10n.T("undo.reselect"), l =>
            {
                l.mask.source = MaskSource.Selection;
                l.mask.selectedBits = bits;
                l.mask.paintRle = paint;
                l.mask.embedded = System.Array.Empty<byte>();
                l.mask.embeddedSize = 0;
            });
            Leave();
            Notifier.Post(MessageLevel.Success, L10n.T("status.reselected"), origin);
            return true;
        }

        /// <summary>選択をやめてレイヤーの調整へ戻る（レイヤーは変えない）。</summary>
        public static void Cancel()
        {
            if (!IsSelecting) return;
            SessionController.Current?.Paint?.DiscardStroke();
            Leave();
        }

        /// <summary>作業の開始・終了時にモードを戻す。</summary>
        public static void Reset()
        {
            if (Mode != DtxMode.LayerEdit) SettingsService.Set(SettingKeys.EditMode, DtxMode.LayerEdit);
            if (!string.IsNullOrEmpty(EditingLayerId)) SettingsService.Set(SettingKeys.EditingLayerId, string.Empty);
        }

        private static void Enter(DtxMode mode, string layerId)
        {
            SettingsService.Set(SettingKeys.EditingLayerId, layerId);
            SettingsService.Set(SettingKeys.EditMode, mode);
            SettingsService.Set(SettingKeys.IsPaintMode, false);
            SettingsService.Set(SettingKeys.ScenePickPaused, false);
            if (ToolManager.activeToolType != typeof(DtxSelectTool)) ToolManager.SetActiveTool<DtxSelectTool>();
            SceneView.RepaintAll();
        }

        private static void Leave()
        {
            Reset();
            SceneView.RepaintAll();
        }

        /// <summary>描きかけの手塗りがあれば確定してから読む。</summary>
        private static void CommitPendingStroke(WorkSession s)
        {
            if (s.Paint == null) return;
            PaintCommit.Commit(s);
        }

        /// <summary>レイヤーの番号から決まる初期色（色相を黄金角ずつ回す）。</summary>
        public static Color NextLayerColor(int index)
        {
            float hue = Mathf.Repeat(index * 0.618034f, 1f);
            var c = Color.HSVToRGB(hue, 0.45f, 0.85f);
            c.a = 1f;
            return c;
        }
    }
}
