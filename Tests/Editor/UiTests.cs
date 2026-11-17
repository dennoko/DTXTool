using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Dennoko.DTXTool.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dennoko.DTXTool.Tests
{
    /// <summary>
    /// ウィンドウとシーンのカプセルが、Idle / 作業中 / 範囲選択中のどの状態でも組み立てられること。
    /// 見た目は Unity 上で目で確かめるものなので、ここでは「UXML の名前と C# の対応が取れている」「状態に応じて出し分ける」までを見る。
    /// </summary>
    public sealed class UiTests
    {
        private GameObject _go;
        private Mesh _mesh;
        private Material _material;

        [SetUp]
        public void SetUp()
        {
            if (SessionController.IsWorking) SessionController.End();
            _material = new Material(Shader.Find("Standard"));
            _mesh = new Mesh
            {
                vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
            _mesh.RecalculateNormals();
            _go = new GameObject("DtxUiTestTarget");
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _go.AddComponent<MeshRenderer>().sharedMaterial = _material;
            string key = TargetKey.For(_go.GetComponent<Renderer>());
            if (Directory.Exists(StoragePaths.TargetDir(key))) Directory.Delete(StoragePaths.TargetDir(key), true);
            DocumentCommands.ResetForTarget(string.Empty, new DtxDocument());
        }

        [TearDown]
        public void TearDown()
        {
            if (SessionController.IsWorking) SessionController.End();
            if (EditorWindow.HasOpenInstances<DTXToolWindow>()) EditorWindow.GetWindow<DTXToolWindow>().Close();
            if (_go != null) Object.DestroyImmediate(_go);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
            if (_material != null) Object.DestroyImmediate(_material);
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(TempTexturePath) != null) AssetDatabase.DeleteAsset(TempTexturePath);
            SettingsService.ResetToLower(SettingKeys.Language);
            SettingsService.ResetToLower(SettingKeys.ExportBaseMap);
            SettingsService.ResetToLower(SettingKeys.ExportNormalMap);
            SettingsService.ResetToLower(SettingKeys.DefaultEditMode);
        }

        private const string TempTexturePath = "Assets/DtxUiTestTexture.asset";

        private static bool Shown(VisualElement root, string name)
        {
            var e = root.Q(name);
            Assert.IsNotNull(e, "element not found: " + name);
            return e.style.display != DisplayStyle.None;
        }

        private static VisualElement Rebuild(DTXToolWindow window)
        {
            // 表示は状態から作り直せる（UI は値を持たない）ので、作り直して今の状態の表示を得る
            window.CreateGUI();
            return window.rootVisualElement;
        }

        [Test]
        public void Assets_AreFoundByGuid()
        {
            Assert.IsNotNull(UiAssets.LoadTree(UiAssets.WindowUxmlGuid), "window UXML");
            Assert.IsNotNull(UiAssets.LoadTree(UiAssets.CapsuleUxmlGuid), "capsule UXML");
            foreach (var guid in new[] { UiAssets.ThemeUssGuid, UiAssets.BlocksUssGuid, UiAssets.StylesUssGuid })
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Assert.IsFalse(string.IsNullOrEmpty(path), "USS guid " + guid);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<StyleSheet>(path), path);
                StringAssert.Contains("DTXTool", path, "must be DTXTool's own copy, not another tool's");
            }
            StringAssert.EndsWith("DTXToolWindow.uxml", AssetDatabase.GUIDToAssetPath(UiAssets.WindowUxmlGuid));
        }

        [TestCase(UILanguage.Japanese)]
        [TestCase(UILanguage.English)]
        public void Window_BuildsInEveryState(UILanguage language)
        {
            SettingsService.Set(SettingKeys.Language, language);
            var window = EditorWindow.GetWindow<DTXToolWindow>();
            var root = Rebuild(window);
            Assert.IsNotNull(root.Q("primary-button"));
            Assert.IsTrue(root.ClassListContains("dennoko-root"), "theme root class");

            // Idle: 対象カード内の作業開始ボタンだけ（フッターは出さない）
            Assert.IsFalse(Shown(root, "session-banner"));
            Assert.IsFalse(Shown(root, "layers-card"));
            Assert.IsFalse(Shown(root, "psd-card"));
            Assert.IsFalse(Shown(root, "output-dock"));
            Assert.IsFalse(Shown(root, "uv-card"));
            Assert.IsFalse(Shown(root, "select-card"));
            Assert.IsFalse(Shown(root, "footer"));
            Assert.IsTrue(Shown(root, "target-start"));
            Assert.AreEqual(L10n.T("button.start"), root.Q<Button>("target-start").text);

            // 作業中（レイヤーなし）
            Assert.IsTrue(SessionController.Start(_go.GetComponent<Renderer>()));
            root = Rebuild(window);
            Assert.IsFalse(Shown(root, "target-start"));
            Assert.IsTrue(Shown(root, "footer"));
            Assert.IsTrue(Shown(root, "session-banner"));
            Assert.IsTrue(Shown(root, "layers-card"));
            Assert.IsTrue(Shown(root, "layers-empty"), "empty state invites the first selection");
            Assert.IsTrue(Shown(root, "psd-card"));
            Assert.IsTrue(Shown(root, "output-dock"), "resolution and file name live above the footer");
            var exportBaseToggle = root.Q<Toggle>("export-base");
            var exportNormalToggle = root.Q<Toggle>("export-normal");
            Assert.IsNotNull(exportBaseToggle);
            Assert.IsNotNull(exportNormalToggle);
            Assert.IsTrue(exportBaseToggle.value);
            Assert.IsTrue(exportNormalToggle.value);
            Assert.IsTrue(root.Q("footer").ClassListContains("dennoko-footer--joined"));
            Assert.IsFalse(Shown(root, "uv-card"), "the UV view is only shown while selecting");
            Assert.IsTrue(Shown(root, "target-details"));
            Assert.IsTrue(root.Q("target-details").ClassListContains("dennoko-details--collapsed"), "rarely used target settings are folded");
            Assert.IsFalse(Shown(root, "layer-card"));
            Assert.IsFalse(Shown(root, "layer-props"));
            Assert.IsFalse(Shown(root, "secondary-button"));
            Assert.AreEqual(L10n.T("button.exportPng"), root.Q<Button>("primary-button").text);
            Assert.Greater(root.Q<DropdownField>("prop-base").choices.Count, 1, "shader properties are listed");
            StringAssert.Contains("_MainTex", root.Q<DropdownField>("prop-base").value);

            // 範囲選択中: UV ビューはスクロール領域の外（上）に固定
            DtxModeController.BeginSelectNew();
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "select-card"));
            Assert.IsTrue(Shown(root, "uv-card"));
            Assert.IsNull(root.Q("main-scroll").Q("uv-card"), "the UV view must not be inside the scroll view");
            Assert.IsFalse(Shown(root, "psd-card"), "only the current task is shown while selecting");
            Assert.IsFalse(Shown(root, "output-dock"));
            Assert.IsTrue(Shown(root, "secondary-button"));
            Assert.AreEqual(L10n.T("button.confirmSelection"), root.Q<Button>("primary-button").text);
            Assert.AreEqual(L10n.T("select.title.new"), root.Q<Label>("select-title").text);

            // レイヤーを作る → 設定カードが出る
            EditCommands.ModifySelection("select", bits => { TriangleBits.Set(bits, 0, true); return true; });
            Assert.IsTrue(DtxModeController.Confirm());
            root = Rebuild(window);
            Assert.IsFalse(Shown(root, "layers-empty"));
            Assert.IsTrue(Shown(root, "layer-props"), "name / blend / opacity are edited in the layer list");
            Assert.IsTrue(Shown(root, "layer-card"));
            Assert.IsTrue(Shown(root, "material-card"));
            Assert.IsFalse(Shown(root, "background-card"));
            Assert.AreEqual(2, root.Q("layer-list").childCount, "one layer row + the background row");
            var layer = DocumentHost.instance.SelectedLayer;
            Assert.AreEqual(layer.name, root.Q<TextField>("layer-name").value);
            Assert.AreEqual(L10n.T("button.reselectArea"), root.Q<Button>("mask-reselect").text);
            Assert.IsTrue(root.Q("mask-details").ClassListContains("dennoko-details--collapsed"), "mask options are folded");
            // 色・テクスチャ・ノーマルの欄は常に出し、配置はテクスチャを入れた時だけ出す
            Assert.IsTrue(Shown(root, "base-color"));
            Assert.IsTrue(Shown(root, "base-texture"));
            Assert.IsTrue(Shown(root, "normal-map"));
            Assert.IsTrue(Shown(root, "replace-normal"));
            Assert.IsFalse(Shown(root, "base-placement"), "placement only matters when a texture is used");
            Assert.IsFalse(Shown(root, "normal-options"));
            Assert.IsTrue(root.Q<Button>("size-2048").ClassListContains("dennoko-button-active"));

            // ベースのみモード: ノーマル関連 UI と出力対象チェックボックスを隠してコンパクトにする
            DocumentCommands.Edit("base only", d => d.editMode = EditMode.BaseOnly);
            root = Rebuild(window);
            Assert.IsFalse(Shown(root, "normal-map"));
            Assert.IsFalse(Shown(root, "replace-normal"));
            Assert.IsFalse(Shown(root, "prop-normal"));
            Assert.IsFalse(Shown(root, "output-targets"));
            DocumentCommands.Edit("base and normal", d => d.editMode = EditMode.BaseAndNormal);
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "normal-map"));
            Assert.IsTrue(Shown(root, "output-targets"));

            // テクスチャとノーマル（タイリング）: それぞれの欄の下に配置が出る。同じ配置にするは既定でオフ
            var tex = new Texture2D(4, 4);
            AssetDatabase.CreateAsset(tex, TempTexturePath);
            var texRef = TextureInfo.ToRef(tex);
            DocumentCommands.EditSelected("setup", l =>
            {
                l.baseTexture = texRef;
                l.normalTexture = texRef.Clone();
                l.mask.feather = 8f / 2048f;
            });
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "base-placement"));
            Assert.IsTrue(Shown(root, "tiling-group"));
            Assert.IsTrue(Shown(root, "normal-options"));
            Assert.IsFalse(Shown(root, "normal-mode-row"), "the mode is chosen under the main texture");
            Assert.IsTrue(Shown(root, "sync-normal"));
            Assert.IsFalse(root.Q<Toggle>("sync-normal").value, "separate placement by default");
            Assert.IsTrue(Shown(root, "normal-xform-group"));
            Assert.AreEqual(8f, root.Q<Slider>("mask-feather").value, 0.01f, "feather is shown in pixels at the output size");

            // デカール
            DocumentCommands.EditSelected("decal", l => l.placementMode = PlacementMode.Decal);
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "decal-group"));
            Assert.IsFalse(Shown(root, "tiling-group"));
            Assert.IsFalse(Shown(root, "sync-normal"));
            Assert.IsFalse(Shown(root, "normal-xform-group"));
            Assert.IsFalse(Shown(root, "normal-decal-group"), "the decal is placed under the main texture");

            // ノーマルだけ: 方式とデカールの位置はノーマル側に出る
            DocumentCommands.EditSelected("normal only", l => l.baseTexture = new TextureRef());
            root = Rebuild(window);
            Assert.IsFalse(Shown(root, "base-placement"));
            Assert.IsTrue(Shown(root, "normal-mode-row"));
            Assert.IsTrue(Shown(root, "normal-decal-group"));

            // 下地を選ぶ
            DocumentCommands.Select(DocumentHost.BackgroundId);
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "background-card"));
            Assert.IsFalse(Shown(root, "layer-card"));

            // 範囲の再調整
            DtxModeController.BeginReselect(layer.id);
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "select-card"));
            Assert.IsTrue(Shown(root, "layer-card"), "mask settings stay available while adjusting the area");
            StringAssert.Contains(layer.name, root.Q<Label>("select-title").text);
            DtxModeController.Cancel();
        }

        [Test]
        public void Window_FieldsWriteThroughDocumentCommands()
        {
            Assert.IsTrue(SessionController.Start(_go.GetComponent<Renderer>()));
            var layer = DocumentCommands.AddSelectionLayer("add", "L", new[] { 1 }, null);
            var window = EditorWindow.GetWindow<DTXToolWindow>();
            var root = Rebuild(window);

            root.Q<Slider>("layer-opacity").value = 0.25f;
            Assert.AreEqual(0.25f, DocumentHost.instance.Doc.Find(layer.id).opacity, 1e-4f);
            root.Q<Toggle>("mask-invert").value = true;
            Assert.IsTrue(DocumentHost.instance.Doc.Find(layer.id).mask.invert);
            root.Q<Slider>("mask-feather").value = 16f;
            Assert.AreEqual(16f / 2048f, DocumentHost.instance.Doc.Find(layer.id).mask.feather, 1e-6f);
            root.Q<DropdownField>("layer-blend").index = 3;
            Assert.AreEqual(BlendMode.Screen, DocumentHost.instance.Doc.Find(layer.id).blendMode);
            root.Q<UnityEditor.UIElements.ColorField>("base-color").value = new Color(0.1f, 0.2f, 0.3f, 1f);
            Assert.AreEqual(0.2f, DocumentHost.instance.Doc.Find(layer.id).color.g, 1e-4f);
            root.Q<Vector2Field>("base-tiling").value = new Vector2(3f, 4f);
            Assert.AreEqual(new Vector2(3f, 4f), DocumentHost.instance.Doc.Find(layer.id).baseXform.tiling);
            root.Q<Toggle>("replace-normal").value = true;
            Assert.IsTrue(DocumentHost.instance.Doc.Find(layer.id).replaceNormal);
        }

        [Test]
        public void SceneCapsule_BuildsWhileWorking()
        {
            Assert.IsTrue(SessionController.Start(_go.GetComponent<Renderer>()));
            var capsule = new CapsuleOverlay();
            Assert.IsTrue(capsule.visible);
            var root = capsule.CreatePanelContent();
            Assert.IsNotNull(root.Q<Button>("confirm"));
            Assert.IsNotNull(root.Q<Button>("begin-select"));
            Assert.IsTrue(Shown(root, "seg-layer"));
            Assert.IsFalse(Shown(root, "seg-confirm"), "confirm / cancel only while selecting");

            DtxModeController.BeginSelectNew();
            root = capsule.CreatePanelContent();
            Assert.IsFalse(Shown(root, "seg-layer"));
            Assert.IsTrue(Shown(root, "seg-confirm"));
            DtxModeController.Cancel();
        }

        [Test]
        public void L10n_EveryKeyUsedInCodeHasBothLanguages()
        {
            // ソース中の L10n.T("…") / L10n.F("…") のキーが、日本語・英語の両方で定義されていること
            string dir = Path.GetDirectoryName(AssetDatabase.GUIDToAssetPath(UiAssets.StylesUssGuid));
            string editorRoot = Path.GetFullPath(Path.Combine(dir, "..", ".."));
            var keys = new HashSet<string>();
            foreach (var file in Directory.GetFiles(editorRoot, "*.cs", SearchOption.AllDirectories))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), "L10n\\.[TF]\\(\"([^\"]+)\""))
                    keys.Add(m.Groups[1].Value);
            Assert.Greater(keys.Count, 100);

            foreach (var language in new[] { UILanguage.Japanese, UILanguage.English })
            {
                SettingsService.Set(SettingKeys.Language, language);
                foreach (var key in keys)
                {
                    string text = L10n.T(key);
                    Assert.AreNotEqual(key, text, "missing text for " + key + " (" + language + ")");
                    Assert.IsFalse(string.IsNullOrEmpty(text), key);
                }
            }
        }
    }
}
