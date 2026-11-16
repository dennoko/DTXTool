using System.Collections.Generic;
using Dennoko.DTXTool.Core;
using UnityEditor;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// ユーザー操作の意味づけ（Design architecture §5.2）。UI・ショートカット・シーン入力はここだけを呼ぶ。
    /// 作業中でなければ何もしない。
    /// </summary>
    internal static class SessionCommands
    {
        private static WorkSession S => SessionController.Current;

        // ── モード・ツール（Session 層の設定を変えるだけ。表示は Changed で揃う） ──

        public static void SetPaintMode(bool on) => SettingsService.Set(SettingKeys.IsPaintMode, on);

        public static void TogglePaintMode() => SetPaintMode(!SettingsService.Get(SettingKeys.IsPaintMode));

        public static void SetGranularity(SelectionGranularity g)
        {
            SettingsService.Set(SettingKeys.Granularity, g);
            SetPaintMode(false);
        }

        public static void SetPaintTool(PaintSubMode tool)
        {
            SettingsService.Set(SettingKeys.PaintTool, tool);
            SetPaintMode(true);
        }

        public static void ToggleSymmetry() => Toggle(SettingKeys.SymmetryEnabled);

        public static void ToggleXRay() => Toggle(SettingKeys.OverlayOnTop);

        public static void ToggleIsolation()
        {
            Toggle(SettingKeys.IsolateTargetMesh);
            if (S != null) S.Isolation.Apply(S.Target.gameObject, SettingsService.Get(SettingKeys.IsolateTargetMesh));
        }

        /// <summary>一時停止ボタン。Unity の他ツールで止まっている時は作業ツールに戻す。</summary>
        public static void TogglePause()
        {
            if (S == null) return;
            if (UnityEditor.EditorTools.ToolManager.activeToolType != typeof(DtxSelectTool))
            {
                UnityEditor.EditorTools.ToolManager.SetActiveTool<DtxSelectTool>();
                SettingsService.Set(SettingKeys.ScenePickPaused, false);
                return;
            }
            Toggle(SettingKeys.ScenePickPaused);
        }

        public static bool IsPaused
        {
            get
            {
                if (S == null) return true;
                // レイヤーの調整中はシーンのクリックを奪わない（範囲を選んでいる間だけ選択に使う）
                return !DtxModeController.IsSelecting
                       || SettingsService.Get(SettingKeys.ScenePickPaused)
                       || S.Interaction.SpaceHeld
                       || UnityEditor.EditorTools.ToolManager.activeToolType != typeof(DtxSelectTool);
            }
        }

        public static void SetBrushSize(int px)
            => SettingsService.Set(SettingKeys.BrushSize, UnityEngine.Mathf.Clamp(px, SettingKeys.MinBrushSize, SettingKeys.MaxBrushSize));

        private static void Toggle(Setting<bool> s) => SettingsService.Set(s, !SettingsService.Get(s));

        // ── 選択 ──

        /// <summary>現在の粒度で、三角形 t を含むグループ。</summary>
        public static int GroupOf(int triangle) => S.Groups.TriToGroup[triangle];

        /// <summary>対称グループ（無ければ -1）。</summary>
        public static int MirrorOf(int group, GroupTable table)
            => S != null && S.SymmetryOn ? MirrorResolver.Resolve(table, S.Analysis.MirrorTri, group) : -1;

        /// <summary>グループ（と対称グループ）を追加 / 除外する。変わった時だけ履歴に積む。</summary>
        public static bool ApplyGroups(IReadOnlyList<int> groups, bool add, GroupTable table, bool includeMirror)
        {
            if (S == null || groups.Count == 0) return false;
            return EditCommands.ModifySelection(L10n.T("undo.select"), bits =>
            {
                bool changed = false;
                foreach (int g in groups)
                {
                    changed |= SelectionOps.SetGroup(bits, table, g, add);
                    if (!includeMirror) continue;
                    int m = MirrorOf(g, table);
                    if (m >= 0) changed |= SelectionOps.SetGroup(bits, table, m, add);
                }
                return changed;
            });
        }

        public static void SelectAll()
        {
            if (S == null) return;
            EditCommands.ModifySelection(L10n.T("undo.select"), bits => SelectionOps.SelectAll(bits, S.Active));
        }

        public static void InvertSelection()
        {
            if (S == null) return;
            EditCommands.ModifySelection(L10n.T("undo.select"), bits => SelectionOps.Invert(bits, S.Active));
        }

        public static void Grow(bool grow)
        {
            if (S == null) return;
            var a = S.Analysis;
            EditCommands.ModifySelection(L10n.T("undo.select"), bits => grow
                ? SelectionOps.Grow(bits, S.Active, S.Groups, a.AdjStart, a.AdjTris)
                : SelectionOps.Shrink(bits, S.Active, S.Groups, a.AdjStart, a.AdjTris));
        }

        public static void ClearAll(MessageOrigin origin)
        {
            if (S == null) return;
            S.Paint.DiscardStroke();
            if (!EditCommands.ClearAll(L10n.T("undo.clear"))) return;
            Notifier.Post(new UserMessage
            {
                Level = MessageLevel.Info,
                Text = L10n.T("status.cleared"),
                Origin = origin,
                ActionLabel = L10n.T("term.undo"),
                Action = _ => Undo.PerformUndo(),
            });
        }

        // ── 範囲選択の確定・取消（DtxModeController に委ねる） ──

        public static void ConfirmSelection(MessageOrigin origin)
        {
            if (S == null) return;
            DtxModeController.Confirm(origin);
        }

        public static void CancelSelection()
        {
            if (S == null) return;
            DtxModeController.Cancel();
        }

        // ── 出力（Export/ に委ねる） ──

        public static void ExportPng(MessageOrigin origin)
        {
            if (S == null) return;
            if (DtxModeController.IsSelecting) DtxModeController.Cancel();
            PngExporter.Export(S, origin);
        }

        public static void ExportPsd(string filePath, MessageOrigin origin)
        {
            if (S == null) return;
            if (DtxModeController.IsSelecting) DtxModeController.Cancel();
            PsdExporter.Export(S, filePath, origin);
        }

        public static void ImportPsd(string filePath, MessageOrigin origin)
        {
            if (S == null) return;
            PsdImporter.Import(S, filePath, origin);
        }

        public static void ImportMask(string assetPath, MessageOrigin origin)
        {
            if (S == null) return;
            MaskImporter.Import(S, assetPath, origin);
        }
    }
}
