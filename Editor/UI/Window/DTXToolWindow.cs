using System.Collections.Generic;
using System.IO;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// ツールのウィンドウ。skill の画面パターン P4（シーン作業）: 状態は Idle（未開始）と Working（作業中）。
    ///   Idle    : B1 対象カードと主ボタン「作業開始」だけ
    ///   Working : B7 作業中バナー + B1 + レイヤー一覧 + 選択中レイヤーのマスク・質感 + PSD カード、出力ドック（解像度・ファイル名）
    ///             主ボタンは「PNG 出力」。範囲を選んでいる間だけ「選択を確定」に替わる（主ボタンは常に 1 つ）
    ///             範囲を選んでいる間は B3 UV ビューをスクロール領域の外（上）に固定し、出力ドックと PSD カードを隠す
    /// 状態を持たず、表示はすべて DocumentHost / SettingsService / SessionController から作る。
    /// ウィンドウを閉じると作業を終え、プレビューを元のテクスチャへ戻す（リロードでは OnDestroy は呼ばれない）。
    /// </summary>
    internal sealed class DTXToolWindow : EditorWindow
    {
        public const string ToolName = "DTXTool";
        // バージョンはツール直下の version.json（DTXToolVersion.Current）

        private DennokoAppBar _appBar;
        private Label _versionLabel;
        private DennokoStatus _status;
        private Button _statusAction;
        private string _statusAsset;
        private int _statusSerial;   // 後から来たメッセージを、前のメッセージの時間切れで消さないための番号

        [MenuItem("dennokoworks/DTXTool")]
        public static void Open()
        {
            var w = GetWindow<DTXToolWindow>();
            w.titleContent = new GUIContent(ToolName);
            w.minSize = new Vector2(340, 440);
        }

        /// <summary>右クリック・自動再開からウィンドウを開く。bringToFront = true なら、開いているウィンドウを最前面にする。</summary>
        public static void EnsureOpen(bool bringToFront = false)
        {
            if (HasOpenInstances<DTXToolWindow>())
            {
                if (bringToFront)
                {
                    var existing = GetWindow<DTXToolWindow>(false, ToolName, false);
                    existing.Show();
                    existing.Focus();
                }
                return;
            }
            var w = GetWindow<DTXToolWindow>(false, ToolName, bringToFront);
            w.minSize = new Vector2(340, 440);
            if (bringToFront)
            {
                w.Show();
                w.Focus();
            }
        }

        private void OnEnable()
        {
            Notifier.Posted += OnMessage;
            DennokoSceneSession.Ended += OnSessionEnded;
        }

        private void OnDisable()
        {
            Notifier.Posted -= OnMessage;
            DennokoSceneSession.Ended -= OnSessionEnded;
        }

        private void OnDestroy()
        {
            SessionController.End(reason: DennokoSessionEndReason.WindowClosed);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            UiAssets.SetupRoot(root);
            root.style.flexGrow = 1;
            var tree = UiAssets.LoadTree(UiAssets.WindowUxmlGuid);
            if (tree == null)
            {
                root.Add(new Label("UXML Asset not found."));
                return;
            }
            // 表示言語が変わったら中身ごと作り直す
            LanguageUi.Build(root, content => Build(content, tree));
        }

        private void Build(VisualElement root, VisualTreeAsset tree)
        {
            tree.CloneTree(root);

            new TargetCard(root);
            new TargetPropertyRows(root);
            new LayersCard(root);
            new LayerInspector(root);
            new UVCard(root);
            new DtxOutputCard(root);
            BindAppBar(root);
            BindSessionBanner(root);
            BindSelectCard(root);
            BindFooter(root);

            _status = new DennokoStatus(root.Q<Label>("status-label"), "Ready", 4000);
            _status.Ready();
            root.Q<Label>("status-label").RegisterCallback<ClickEvent>(_ => PingStatus());
            _statusAction = root.Q<Button>("status-action");
            Bind.Show(_statusAction, false);

            UiHub.Watch(root, () =>
            {
                bool working = UiRules.Working;
                bool selecting = working && DtxModeController.IsSelecting;
                Bind.Show(root.Q("session-banner"), working);
                Bind.Show(root.Q("select-card"), selecting);
                Bind.Show(root.Q("layers-card"), working);
                // UV ビューは範囲を選んでいる間だけ、スクロール領域の外（上）に固定して出す
                Bind.Show(root.Q("uv-card"), selecting);
                // 範囲を選んでいる間は、出力の設定を出さない（今やることだけを見せる）
                bool output = working && !selecting;
                Bind.Show(root.Q("psd-card"), output);
                Bind.Show(root.Q("output-dock"), output);
                Bind.Show(root.Q("footer"), working);
                root.Q("footer").EnableInClassList("dennoko-footer--joined", output);
                Bind.Show(root.Q("overwrite-output"), output);
            });
        }

        // ── 標準ヘッダー（トップバー） ──

        private void BindAppBar(VisualElement root)
        {
            var appBar = _appBar = new DennokoAppBar(root, ToolName, english: !L10n.Japanese)
            {
                AddToolMenuItems = menu =>
                {
                    menu.AddItem(new GUIContent(L10n.T("menu.preferences")), false, () => OpenSettings(true));
                    menu.AddItem(new GUIContent(L10n.T("menu.projectSettings")), false, () => OpenSettings(false));
                },
            };
            // 言語は SettingsService に持つ。切り替えると LanguageUi が中身ごと作り直す
            appBar.LanguageChanged += LanguageUi.SetEnglish;
            root.Q<Button>("appbar-settings").tooltip = L10n.T("tooltip.menu");
            // ここで例外が出ると後のバインド（作業終了ボタン・フッター）が丸ごと抜けるので、要素が無くても落ちないようにする
            var reload = root.Q<Button>("version-reload-button");
            if (reload != null) reload.tooltip = L10n.T("tooltip.reload");

            // バージョン表示と更新チェック（GitHub の version.json と比べる。取得は DTXToolVersion が間隔を空けて行う）
            _versionLabel = root.Q<Label>("version-label");
            appBar.CheckUpdateRequested += () =>
            {
                DTXToolVersion.ForceRecheck();
                LoadVersionResultFromSessionState(); // すぐ「確認中…」にする
            };
            LoadVersionResultFromSessionState();
            DTXToolVersion.StartCheckBackgroundTask();
        }

        /// <summary>
        /// 取得済みの最新版と今のローカル版から表示を作る（DTXToolVersion が取得後にも呼ぶ）。
        /// 更新の有無は保存せず毎回比べ直す（ローカル版が後から読めた時に矛盾した表示を残さない）。
        /// </summary>
        internal void LoadVersionResultFromSessionState()
        {
            if (_versionLabel == null) return;
            string local = DTXToolVersion.Current;
            string latest = SessionState.GetString(DTXToolVersion.VerCheckLatestKey, string.Empty);
            bool done = SessionState.GetBool(DTXToolVersion.VerCheckDoneKey, false);
            bool error = SessionState.GetBool(DTXToolVersion.VerCheckErrorKey, false);
            string url = SessionState.GetString(DTXToolVersion.VerCheckUrlKey, string.Empty);

            string text = "v" + local;
            bool update = false;
            bool failed = false;
            if (!done)
            {
                text += "  " + L10n.T("status.version.checking");
            }
            else if (error || string.IsNullOrEmpty(latest))
            {
                text += "  " + L10n.T("status.version.error");
                failed = true;
            }
            else if (DennokoVersionChecker.IsUpdateAvailable(latest, local))
            {
                text += "  " + L10n.F("status.version.update", latest);
                update = true;
            }
            _versionLabel.text = text;
            _versionLabel.EnableInClassList("dennoko-version-label--update", update);
            _versionLabel.EnableInClassList("dennoko-version-label--error", failed);

            // 更新ありで配布ページ（version.json の url）がある時だけ、クリックで開く。GitHub への直接の導線は作らない
            bool canOpen = update && !string.IsNullOrEmpty(url) && url.IndexOf("github.com", System.StringComparison.OrdinalIgnoreCase) < 0;
            if (_appBar != null)
            {
                _appBar.ReleaseUrl = canOpen ? url : null;
                _appBar.SetVersionClickable(canOpen, canOpen ? L10n.T("tooltip.openRelease") : null);
            }
        }

        private static void OpenSettings(bool user)
        {
            if (user) UnityEditor.SettingsService.OpenUserPreferences(DtxSettingsPages.PreferencesPath);
            else UnityEditor.SettingsService.OpenProjectSettings(DtxSettingsPages.ProjectPath);
        }

        // ── B7 作業中バナー ──

        private static void BindSessionBanner(VisualElement root)
        {
            root.Q<Label>("session-state").text = L10n.T("session.state");
            root.Q<Label>("session-hint").text = L10n.T("session.hint");
            var end = root.Q<Button>("session-end");
            end.text = L10n.T("button.end");
            end.clicked += () => SessionController.End();

            var target = root.Q<Label>("session-target");
            target.tooltip = L10n.T("tooltip.frameTarget");
            target.RegisterCallback<ClickEvent>(_ => DennokoSceneSession.FrameTarget());
            UiHub.Watch(target, () => target.text = SessionController.Current?.Target != null ? SessionController.Current.Target.name : string.Empty);
        }

        // ── 範囲選択の案内（範囲を選んでいる間だけ） ──

        private static void BindSelectCard(VisualElement root)
        {
            var title = root.Q<Label>("select-title");
            var selectAll = root.Q<Button>("select-all");
            selectAll.text = L10n.T("button.selectAll");
            selectAll.tooltip = L10n.T("tooltip.selectAll");
            selectAll.clicked += SessionCommands.SelectAll;
            root.Q<Label>("select-hint").text = L10n.T("select.hint");
            var keys = root.Q("select-keys");
            DennokoBlockUI.AddKeyHint(keys, "Shift", L10n.T("select.key.add"));
            DennokoBlockUI.AddKeyHint(keys, "Ctrl", L10n.T("select.key.remove"));
            DennokoBlockUI.AddKeyHint(keys, "Enter", L10n.T("select.key.confirm"));
            DennokoBlockUI.AddKeyHint(keys, "Esc", L10n.T("select.key.cancel"));
            UiHub.Watch(title, () =>
            {
                if (DtxModeController.Mode == DtxMode.Reselect)
                {
                    var layer = DocumentHost.instance.Doc.Find(DtxModeController.EditingLayerId);
                    title.text = L10n.F("select.title.reselect", layer != null ? layer.name : string.Empty);
                }
                else
                {
                    title.text = L10n.T("select.title.new");
                }
            });
        }

        // ── フッター（主ボタン 1 つ。Idle = 作業開始 / 選択中 = 選択を確定 / それ以外 = PNG 出力） ──

        private static void BindFooter(VisualElement root)
        {
            var primary = root.Q<Button>("primary-button");
            var secondary = root.Q<Button>("secondary-button");
            var overwrite = root.Q<Toggle>("overwrite-output");
            overwrite.text = L10n.T("output.overwrite");
            overwrite.tooltip = L10n.T("tooltip.overwrite");
            Bind.Toggle(overwrite, SettingKeys.OverwriteOutput);
            secondary.text = L10n.T("button.cancel");
            secondary.clicked += SessionCommands.CancelSelection;
            primary.clicked += () =>
            {
                if (!SessionController.IsWorking) SessionController.Start(TargetCard.PendingTarget);
                else if (DtxModeController.IsSelecting) SessionCommands.ConfirmSelection(MessageOrigin.Window);
                else SessionCommands.ExportPng(MessageOrigin.Window);
            };
            UiHub.Watch(primary, () =>
            {
                bool working = UiRules.Working;
                bool selecting = working && DtxModeController.IsSelecting;
                bool baseOnly = DocumentHost.instance.Doc.editMode == Core.EditMode.BaseOnly;
                bool canExport = baseOnly || SettingsService.Get(SettingKeys.ExportBaseMap) || SettingsService.Get(SettingKeys.ExportNormalMap);
                primary.text = selecting ? L10n.T("button.confirmSelection") : L10n.T("button.exportPng");
                primary.SetEnabled(working && (selecting || canExport));
                // 押せない理由はツールチップに出す
                primary.tooltip = working && !selecting && !canExport ? L10n.T("tooltip.chooseExportTarget") : string.Empty;
                Bind.Show(secondary, selecting);
            });
        }

        // ── ステータスバー（ウィンドウから実行した結果。シーンから実行した結果はトースト） ──

        private void OnMessage(UserMessage m)
        {
            if (m.Origin == MessageOrigin.Scene && SceneView.lastActiveSceneView != null && SessionController.IsWorking) return;
            if (_status == null) return;
            int serial = ++_statusSerial;
            // 操作ボタンが付いているときは少し長く出す
            _status.AutoResetMs = m.Action != null ? 8000 : 4000;
            _status.Set(m.Text, ToStatusType(m.Level));
            _statusAsset = m.AssetPath;

            _statusAction.clickable = new Clickable(() => { });
            Bind.Show(_statusAction, m.Action != null);
            if (m.Action != null)
            {
                _statusAction.text = m.ActionLabel;
                var action = m.Action;
                _statusAction.clickable = new Clickable(() =>
                {
                    Bind.Show(_statusAction, false);
                    action(_statusAction);
                });
            }

            // DennokoStatus は Info を自動では戻さないので、ここで Ready に戻す。操作ボタンは、文が消えるのと一緒に隠す
            _statusAction.schedule.Execute(() =>
            {
                if (serial != _statusSerial) return;
                if (m.Level == MessageLevel.Info) ClearStatus();
                else Bind.Show(_statusAction, false);
            }).StartingIn(_status.AutoResetMs);
        }

        /// <summary>共通の編集セッションが終わった理由（リロード・Play モードは自動で再開するので出さない）。</summary>
        private void OnSessionEnded(DennokoSessionEndReason reason)
        {
            if (_status == null) return;
            string key = reason switch
            {
                DennokoSessionEndReason.Escape => "session.end.escape",
                DennokoSessionEndReason.SceneChanged => "session.end.sceneChanged",
                DennokoSessionEndReason.TargetLost => "session.end.targetLost",
                DennokoSessionEndReason.Replaced => "session.end.replaced",
                _ => null,
            };
            if (key == null) return;
            _statusSerial++;
            _status.AutoResetMs = 4000;
            _status.Set(L10n.T(key), DennokoStatusType.Warning);
            Bind.Show(_statusAction, false);
            _statusAsset = null;
        }

        private static DennokoStatusType ToStatusType(MessageLevel level) => level switch
        {
            MessageLevel.Success => DennokoStatusType.Success,
            MessageLevel.Warning => DennokoStatusType.Warning,
            MessageLevel.Error => DennokoStatusType.Error,
            _ => DennokoStatusType.Info,
        };

        private void ClearStatus()
        {
            _status.Ready();
            Bind.Show(_statusAction, false);
            _statusAsset = null;
        }

        private void PingStatus()
        {
            if (string.IsNullOrEmpty(_statusAsset)) return;
            var obj = AssetDatabase.LoadAssetAtPath<Object>(_statusAsset);
            if (obj != null) EditorGUIUtility.PingObject(obj);
        }
    }

    /// <summary>
    /// B1 対象カードの追加行: プレビューを入れるシェーダープロパティ（BaseMap / NormalMap）。
    /// 既定は自動判別。合わない時だけドロップダウンで差し替える（対象ごとに覚える）。
    /// </summary>
    internal sealed class TargetPropertyRows
    {
        private const string DetailsOpenKey = "DTXTool.TargetDetailsOpen";

        private readonly VisualElement _group;
        private readonly DropdownField _base, _normal;

        public TargetPropertyRows(VisualElement root)
        {
            // プロパティとメッシュの情報は「▶ 詳細」に畳む（開閉はエディターを閉じるまで覚える）
            var details = root.Q("target-details");
            root.Q<Label>("target-details-title").text = L10n.T("details.title");
            DennokoBlockUI.BindDetails(details, SessionState.GetBool(DetailsOpenKey, false), open => SessionState.SetBool(DetailsOpenKey, open));
            UiHub.Watch(details, () => Bind.Show(details, SessionController.Current != null));

            _group = root.Q("target-props");
            _base = root.Q<DropdownField>("prop-base");
            _normal = root.Q<DropdownField>("prop-normal");
            _base.tooltip = L10n.T("tooltip.propBase");
            _normal.tooltip = L10n.T("tooltip.propNormal");
            _base.RegisterValueChangedCallback(e => { if (e.target == _base) Choose(SettingKeys.BaseProperty, _base); });
            _normal.RegisterValueChangedCallback(e => { if (e.target == _normal) Choose(SettingKeys.NormalProperty, _normal); });
            UiHub.Watch(_group, Refresh);
        }

        /// <summary>先頭の項目が「自動」。それ以外はプロパティ名そのもの。</summary>
        private static void Choose(Setting<string> setting, DropdownField field)
        {
            var s = SessionController.Current;
            if (s == null || field.index < 0) return;
            if (field.index == 0) SettingsService.ResetToLower(setting);
            else SettingsService.Set(setting, field.choices[field.index]);
            s.BeginPreview();
        }

        private void Refresh()
        {
            var s = SessionController.Current;
            Bind.Show(_group, s != null);
            if (s == null) return;
            bool baseOnly = DocumentHost.instance.Doc.editMode == Core.EditMode.BaseOnly;
            Bind.Show(_normal, !baseOnly);
            var material = s.Injector.Active ? s.Injector.Material : s.TargetMaterial;
            var props = ShaderPropertyResolver.TextureProperties(material);
            Fill(_base, props, ShaderPropertyResolver.AutoBase(material), SettingsService.Get(SettingKeys.BaseProperty));
            Fill(_normal, props, ShaderPropertyResolver.AutoNormal(material), SettingsService.Get(SettingKeys.NormalProperty));
        }

        private static void Fill(DropdownField field, List<string> props, string auto, string chosen)
        {
            var choices = new List<string> { L10n.F("target.propAuto", string.IsNullOrEmpty(auto) ? L10n.T("target.propNone") : auto) };
            choices.AddRange(props);
            field.choices = choices;
            int index = string.IsNullOrEmpty(chosen) ? 0 : choices.IndexOf(chosen);
            field.SetValueWithoutNotify(choices[index > 0 ? index : 0]);
        }
    }

    /// <summary>
    /// 出力ドック（PNG 出力の解像度・ファイル名。保存先と実際のファイル名はファイル名欄のツールチップ）と、
    /// PSD カード（PSD の保存 / 読み込み）。PNG 出力はフッターの主ボタン。
    /// </summary>
    internal sealed class DtxOutputCard
    {
        public DtxOutputCard(VisualElement root)
        {
            root.Q<Label>("psd-header").text = L10n.T("psd.header");
            root.Q<Label>("size-label").text = L10n.T("output.size");
            root.Q<Label>("output-name-label").text = L10n.T("output.name");

            var sizes = new List<(Button, int)>();
            foreach (int size in DtxDocument.Sizes) sizes.Add((root.Q<Button>("size-" + size), size));
            DocBind.Choice(sizes, () => DocumentHost.instance.Doc.width,
                v => DocumentCommands.Edit(L10n.T("undo.resolution"), d => DocumentOps.SetSize(d, v)));

            var exportBase = root.Q<Toggle>("export-base");
            exportBase.text = L10n.T("output.exportBase");
            exportBase.tooltip = L10n.T("tooltip.exportBase");
            Bind.Toggle(exportBase, SettingKeys.ExportBaseMap);

            var exportNormal = root.Q<Toggle>("export-normal");
            exportNormal.text = L10n.T("output.exportNormal");
            exportNormal.tooltip = L10n.T("tooltip.exportNormal");
            Bind.Toggle(exportNormal, SettingKeys.ExportNormalMap);

            var targetsRow = root.Q("output-targets");
            UiHub.Watch(targetsRow, () => Bind.Show(targetsRow, DocumentHost.instance.Doc.editMode != Core.EditMode.BaseOnly));

            var name = root.Q<TextField>("output-name");
            Bind.Text(name, SettingKeys.OutputName, () => OutputPaths.BaseName(SessionController.Current));
            UiHub.Watch(name, () =>
            {
                var s = SessionController.Current;
                if (s == null) return;
                bool baseOnly = DocumentHost.instance.Doc.editMode == Core.EditMode.BaseOnly;
                bool eb = baseOnly || SettingsService.Get(SettingKeys.ExportBaseMap);
                bool en = !baseOnly && SettingsService.Get(SettingKeys.ExportNormalMap);
                string folder = OutputPaths.FolderFor(s);
                var (basePath, normalPath) = OutputPaths.PngPaths(folder, OutputPaths.BaseName(s),
                    DocumentHost.instance.Doc, SettingsService.Get(SettingKeys.OverwriteOutput), eb, en);
                string files = (eb ? "\n" + Path.GetFileName(basePath) : string.Empty)
                             + (en ? "\n" + Path.GetFileName(normalPath) : string.Empty);
                name.tooltip = L10n.T("tooltip.outputName") + "\n" + folder + "/" + files;
            });

            var export = root.Q<Button>("psd-export");
            export.text = L10n.T("button.exportPsd");
            export.tooltip = L10n.T("tooltip.exportPsd");
            export.clicked += ExportPsd;
            var import = root.Q<Button>("psd-import");
            import.text = L10n.T("button.importPsd");
            import.tooltip = L10n.T("tooltip.importPsd");
            import.clicked += ImportPsd;
            root.Q<Label>("psd-hint").text = L10n.T("output.psdHint");
        }

        private static void ExportPsd()
        {
            var s = SessionController.Current;
            if (s == null) return;
            string dir = SessionState.GetString(LastPsdDirKey, Path.GetFullPath(OutputPaths.FolderFor(s)));
            if (!Directory.Exists(dir)) dir = Application.dataPath;
            string path = EditorUtility.SaveFilePanel(L10n.T("button.exportPsd"), dir, OutputPaths.BaseName(s), "psd");
            if (string.IsNullOrEmpty(path)) return;
            SessionState.SetString(LastPsdDirKey, Path.GetDirectoryName(path));
            SessionCommands.ExportPsd(path, MessageOrigin.Window);
        }

        private static void ImportPsd()
        {
            var s = SessionController.Current;
            if (s == null) return;
            string dir = SessionState.GetString(LastPsdDirKey, Path.GetFullPath(OutputPaths.FolderFor(s)));
            if (!Directory.Exists(dir)) dir = Application.dataPath;
            string path = EditorUtility.OpenFilePanel(L10n.T("button.importPsd"), dir, "psd");
            if (string.IsNullOrEmpty(path)) return;
            SessionState.SetString(LastPsdDirKey, Path.GetDirectoryName(path));
            SessionCommands.ImportPsd(path, MessageOrigin.Window);
        }

        private const string LastPsdDirKey = "DTXTool.LastPsdDir";
    }
}
