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
            SettingsService.ResetToLower(SettingKeys.Language);
        }

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

            // Idle: 対象カードと主ボタンだけ
            Assert.IsFalse(Shown(root, "session-banner"));
            Assert.IsFalse(Shown(root, "layers-card"));
            Assert.IsFalse(Shown(root, "output-card"));
            Assert.IsFalse(Shown(root, "select-card"));
            Assert.AreEqual(L10n.T("button.start"), root.Q<Button>("primary-button").text);

            // 作業中（レイヤーなし）
            Assert.IsTrue(SessionController.Start(_go.GetComponent<Renderer>()));
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "session-banner"));
            Assert.IsTrue(Shown(root, "layers-card"));
            Assert.IsTrue(Shown(root, "layers-empty"), "empty state invites the first selection");
            Assert.IsTrue(Shown(root, "output-card"));
            Assert.IsTrue(Shown(root, "target-props"));
            Assert.IsFalse(Shown(root, "layer-card"));
            Assert.IsFalse(Shown(root, "secondary-button"));
            Assert.AreEqual(L10n.T("button.exportPng"), root.Q<Button>("primary-button").text);
            Assert.Greater(root.Q<DropdownField>("prop-base").choices.Count, 1, "shader properties are listed");
            StringAssert.Contains("_MainTex", root.Q<DropdownField>("prop-base").value);

            // 範囲選択中
            DtxModeController.BeginSelectNew();
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "select-card"));
            Assert.IsFalse(Shown(root, "output-card"), "only the current task is shown while selecting");
            Assert.IsTrue(Shown(root, "secondary-button"));
            Assert.AreEqual(L10n.T("button.confirmSelection"), root.Q<Button>("primary-button").text);
            Assert.AreEqual(L10n.T("select.title.new"), root.Q<Label>("select-title").text);

            // レイヤーを作る → 設定カードが出る
            EditCommands.ModifySelection("select", bits => { TriangleBits.Set(bits, 0, true); return true; });
            Assert.IsTrue(DtxModeController.Confirm());
            root = Rebuild(window);
            Assert.IsFalse(Shown(root, "layers-empty"));
            Assert.IsTrue(Shown(root, "layer-card"));
            Assert.IsTrue(Shown(root, "material-card"));
            Assert.IsFalse(Shown(root, "placement-card"), "placement only matters when a texture is used");
            Assert.IsFalse(Shown(root, "background-card"));
            Assert.AreEqual(2, root.Q("layer-list").childCount, "one layer row + the background row");
            var layer = DocumentHost.instance.SelectedLayer;
            Assert.AreEqual(layer.name, root.Q<TextField>("layer-name").value);
            Assert.IsTrue(Shown(root, "base-color"));
            Assert.IsFalse(Shown(root, "base-texture"));
            Assert.IsTrue(root.Q<Button>("fill-color").ClassListContains("dennoko-button-active"), "the selected choice is marked");
            Assert.IsTrue(root.Q<Button>("size-2048").ClassListContains("dennoko-button-active"));

            // テクスチャとデカール
            DocumentCommands.EditSelected("setup", l =>
            {
                l.fillKind = FillKind.Texture;
                l.normalKind = NormalKind.Texture;
                l.placementMode = PlacementMode.Decal;
                l.mask.feather = 8f / 2048f;
            });
            root = Rebuild(window);
            Assert.IsTrue(Shown(root, "placement-card"));
            Assert.IsTrue(Shown(root, "decal-group"));
            Assert.IsFalse(Shown(root, "tiling-group"));
            Assert.IsTrue(Shown(root, "base-texture"));
            Assert.IsTrue(Shown(root, "base-tint"));
            Assert.IsTrue(Shown(root, "normal-scale"));
            Assert.AreEqual(8f, root.Q<Slider>("mask-feather").value, 0.01f, "feather is shown in pixels at the output size");

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
