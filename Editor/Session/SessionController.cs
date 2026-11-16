using System;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// 作業の開始・終了・再開の唯一の入口（Design state_and_lifecycle §5）。
    /// Current はリロードで消えるが、SessionLifecycle が再開トークンから作り直す。
    /// </summary>
    internal static class SessionController
    {
        /// <summary>dennokoworks 共通の編集セッション（DennokoSceneSession）での識別名。</summary>
        public const string ToolId = "DTXTool";

        public static WorkSession Current { get; private set; }
        public static bool IsWorking => Current != null;

        /// <summary>開始・終了・対象切替のたびに呼ばれる。</summary>
        public static event Action Changed;

        /// <summary>
        /// 作業を始める。resume = リロード / Play モード後の自動再開（Session 層を保ち、カメラを動かさない）。
        /// keepSession = 対象の切替（Session 層を保つ）。
        /// </summary>
        public static bool Start(Renderer target, bool resume = false, bool keepSession = false, MessageOrigin origin = MessageOrigin.Window)
        {
            if (target == null) return false;
            EndCore(true);

            var tx = new StartTransaction();
            WorkSession session = null;
            try
            {
                string key = TargetKey.For(target);
                SettingsService.LoadTarget(key);
                tx.OnRollback(SettingsService.UnloadTarget);
                if (!resume && !keepSession) SettingsService.ClearSession();

                if (!WorkMeshSources.TryCreate(target, out var mesh, out string meshError))
                    throw new InvalidOperationException(meshError);
                session = new WorkSession(target, key, mesh);
                var created = session;
                tx.OnRollback(created.Dispose);

                session.Analyze(out string uvWarning);
                RestoreEditState(session);
                string restoreInfo = RestoreDocument(session);
                session.CreateGpu();
                if (!resume) DtxModeController.Reset();

                // 1 回合成してから注入する（注入した瞬間に正しい絵が出るように）
                session.Renderer.RenderNow();
                session.BeginPreview();
                tx.OnRollback(created.Injector.End);
                if (_freshDocument) ApplyDefaultBackground(session);

                if (SettingsService.Get(SettingKeys.IsolateTargetMesh)) session.Isolation.Apply(target.gameObject, true);
                tx.OnRollback(created.Isolation.Exit);

                ActivateTool();
                tx.Commit();

                Current = session;
                SceneSessionBridge.OnStarted(target);
                if (!resume && !keepSession)
                {
                    SettingsService.Set(SettingKeys.SymmetryEnabled, session.Analysis.MirrorMatchRate >= 0.9f);
                    FrameTarget(target);
                }

                Changed?.Invoke();
                SceneView.RepaintAll();

                if (uvWarning != null) Notifier.Post(MessageLevel.Warning, uvWarning, origin);
                else if (mesh.Info != null) Notifier.Post(MessageLevel.Info, mesh.Info, origin);
                else if (restoreInfo != null && !resume) Notifier.Post(MessageLevel.Info, restoreInfo, origin);
                return true;
            }
            catch (Exception e)
            {
                tx.Rollback();
                Current = null;
                Debug.LogException(e);
                SceneSessionBridge.OnStartFailed();
                Notifier.Post(MessageLevel.Error, L10n.F("error.startFailed", e.Message), origin);
                Changed?.Invoke();
                return false;
            }
        }

        /// <summary>
        /// 作業中の選択（EditState）。メモリ上のものが同じ対象ならそのまま使い（Undo 履歴も保つ）、違えば空から始める。
        /// 選択は「これからレイヤーにする範囲」の下書きなので、ディスクには保存しない（保存されるのはドキュメント）。
        /// </summary>
        private static void RestoreEditState(WorkSession session)
        {
            var es = EditState.instance;
            var a = session.Analysis;
            if (es.TargetKey == session.TargetKey && es.MeshHash == a.MeshHash && es.TriangleCount == a.TriangleCount
                && es.SelectedBits.Length == TriangleBits.WordCount(a.TriangleCount))
                return;
            EditCommands.ResetForTarget(session.TargetKey, a.MeshHash, a.TriangleCount);
        }

        /// <summary>今回の開始で、ドキュメントを新しく作った（前回の作業が無かった）。</summary>
        private static bool _freshDocument;

        /// <summary>
        /// ドキュメント。メモリ上のものが同じ対象ならそのまま使い（Undo 履歴も保つ）、違えば自動保存から読み、無ければ新しく作る。
        /// メッシュが変わっていたら、選択で作ったレイヤーの範囲は使えないので空にする（戻り値で知らせる）。
        /// </summary>
        private static string RestoreDocument(WorkSession session)
        {
            var host = DocumentHost.instance;
            var a = session.Analysis;
            _freshDocument = false;
            string info = null;
            if (host.TargetKey != session.TargetKey)
            {
                DocumentAutoSave.FlushPending();
                var saved = DocumentAutoSave.TryLoad(session.TargetKey);
                _freshDocument = saved == null;
                var doc = saved ?? new DtxDocument { meshHash = (long)a.MeshHash, triangleCount = a.TriangleCount };
                DocumentCommands.ResetForTarget(session.TargetKey, doc);
                if (saved != null) info = L10n.T("status.restored");
            }
            var current = host.Doc;
            int lost = DocumentOps.AdaptToMesh(current, (long)a.MeshHash, a.TriangleCount);
            current.uvChannel = session.UVChannelUsed;
            current.submeshIndex = session.ActiveSubmesh;
            current.targetName = session.Target.name;
            if (lost > 0)
            {
                host.Touch();
                info = L10n.F("status.meshChangedLayers", lost);
            }
            return info;
        }

        /// <summary>
        /// 新しいドキュメントの下地。マテリアルに元から入っていた BaseMap / NormalMap があればそれを読み込み、解像度も合わせる。
        /// Undo には積まない（作業の出発点）。
        /// </summary>
        private static void ApplyDefaultBackground(WorkSession session)
        {
            var doc = DocumentHost.instance.Doc;
            var baseTex = session.OriginalBaseTexture;
            var normalTex = session.OriginalNormalTexture;
            if (baseTex != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(baseTex)))
            {
                doc.background.baseKind = BackgroundBaseKind.Texture;
                doc.background.baseTexture = TextureInfo.ToRef(baseTex);
                if (!SourceImage.TryGetSourceSize(baseTex, out int w, out int h))
                {
                    w = baseTex.width;
                    h = baseTex.height;
                }
                DocumentOps.SetSize(doc, DocumentOps.NearestSize(Mathf.Max(w, h)));
            }
            if (normalTex != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(normalTex)))
            {
                doc.background.normalKind = BackgroundNormalKind.Texture;
                doc.background.normalTexture = TextureInfo.ToRef(normalTex);
            }
            DocumentHost.instance.Touch();
        }

        /// <summary>作業を終える。何度呼んでもよい。reason は共通の編集セッションへ伝える（ウィンドウのステータスに出す理由）。</summary>
        public static void End(bool save = true, DennokoSessionEndReason reason = DennokoSessionEndReason.User)
        {
            EndCore(save);
            SceneSessionBridge.OnEnded(reason);
        }

        /// <summary>作業だけを終える（対象の切替・やり直しでは共通の編集セッションを続ける）。</summary>
        private static void EndCore(bool save)
        {
            var s = Current;
            if (s == null) return;
            Current = null;
            try
            {
                s.Paint?.DiscardStroke();
                s.Injector.End();
                if (save) DocumentAutoSave.FlushPending();
                SettingsService.FlushTarget();
                RestoreTool();
            }
            finally
            {
                s.Dispose();
                SettingsService.UnloadTarget();
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        /// <summary>同じアバター内の別の Renderer へ切り替える（現在の作業は保存、切替先の前回作業を復元）。</summary>
        public static void SwitchTarget(Renderer target)
        {
            if (target == null || (Current != null && Current.Target == target)) return;
            Start(target, keepSession: true);
        }

        /// <summary>UV チャンネルの変更など、解析をやり直す時（選択と手塗りは EditState にあるので保たれる）。</summary>
        public static void Restart()
        {
            var s = Current;
            if (s == null) return;
            Start(s.Target, resume: true);
        }

        private static void ActivateTool()
        {
            if (ToolManager.activeToolType != typeof(DtxSelectTool)) ToolManager.SetActiveTool<DtxSelectTool>();
        }

        private static void RestoreTool()
        {
            if (ToolManager.activeToolType == typeof(DtxSelectTool)) ToolManager.RestorePreviousPersistentTool();
        }

        private static void FrameTarget(Renderer target)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) return;
            view.Frame(target.bounds, false);
        }
    }
}
