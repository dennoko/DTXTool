using System.IO;
using Dennoko.DTXTool.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Dennoko.DTXTool.Tests
{
    /// <summary>
    /// 作業セッションを通した確認。小さなメッシュ（UV の左半分と右半分に 1 枚ずつの四角形）を対象に、
    /// 開始 → レイヤー作成 → 範囲の再調整 → ベイク → PNG / PSD → 読み込み → 終了（プレビューの復元）まで動かす。
    /// </summary>
    public sealed class SessionTests
    {
        private const string TempDir = "Assets/DtxToolTestTemp";
        private const int Size = 64;

        private GameObject _go;
        private MeshRenderer _renderer;
        private Mesh _mesh;
        private Material _material;
        private string _psdPath;

        [SetUp]
        public void SetUp()
        {
            if (SessionController.IsWorking) SessionController.End();
            if (!AssetDatabase.IsValidFolder(TempDir)) AssetDatabase.CreateFolder("Assets", "DtxToolTestTemp");
            _material = new Material(Shader.Find("Standard")) { name = "DtxTestMaterial" };
            AssetDatabase.CreateAsset(_material, TempDir + "/DtxTestMaterial.mat");

            _mesh = BuildMesh();
            _go = new GameObject("DtxTestTarget");
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _psdPath = Path.Combine(Path.GetTempPath(), "dtxtool_test_" + System.Guid.NewGuid().ToString("N") + ".psd");

            // 前のテストの設定や自動保存を持ち込まない
            string key = TargetKey.For(_renderer);
            if (Directory.Exists(StoragePaths.TargetDir(key))) Directory.Delete(StoragePaths.TargetDir(key), true);
            DocumentCommands.ResetForTarget(string.Empty, new DtxDocument());
        }

        [TearDown]
        public void TearDown()
        {
            if (SessionController.IsWorking) SessionController.End();
            if (_go != null) Object.DestroyImmediate(_go);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
            AssetDatabase.DeleteAsset(TempDir);
            if (File.Exists(_psdPath)) File.Delete(_psdPath);
            PreviewRecoveryJournal.Clear();
        }

        /// <summary>四角形 A = UV の左半分（三角形 0, 1）、四角形 B = 右半分（三角形 2, 3）。</summary>
        private static Mesh BuildMesh()
        {
            var mesh = new Mesh { name = "DtxTestMesh" };
            mesh.vertices = new[]
            {
                new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(-1f, 1f, 0f),
                new Vector3(0.5f, 0f, 0f), new Vector3(1.5f, 0f, 0f), new Vector3(1.5f, 1f, 0f), new Vector3(0.5f, 1f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, 1f),
                new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private WorkSession Start()
        {
            Assert.IsTrue(SessionController.Start(_renderer), "session should start");
            var s = SessionController.Current;
            Assert.IsNotNull(s);
            DocumentCommands.Edit("test size", d =>
            {
                DocumentOps.SetSize(d, Size);
                d.background.baseKind = BackgroundBaseKind.Color;
                d.background.color = Color.blue;
            });
            s.Tick();
            return s;
        }

        private static int[] Bits(WorkSession s, params int[] triangles)
        {
            var bits = TriangleBits.Create(s.Analysis.TriangleCount);
            foreach (int t in triangles) TriangleBits.Set(bits, t, true);
            return bits;
        }

        private static Layer AddRedLayer(WorkSession s, params int[] triangles)
        {
            var layer = DocumentCommands.AddSelectionLayer("test add", "Red", Bits(s, triangles), null);
            DocumentCommands.EditLayer(layer.id, "test color", l => l.color = Color.red);
            s.Tick();
            return layer;
        }

        private static Color32 Pixel(byte[] rgba, int size, int x, int y)
        {
            int i = (y * size + x) * 4;
            return new Color32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
        }

        private static Color32 PreviewPixel(WorkSession s, int x, int y)
        {
            var c = s.Renderer.Compositor;
            return Pixel(GpuReadback.ReadRGBA32(c.BaseResult), c.Size, x, y);
        }

        private static void AssertRed(Color32 c, string message)
        {
            Assert.AreEqual(255, c.r, 2, message);
            Assert.AreEqual(0, c.b, 2, message);
        }

        private static void AssertBlue(Color32 c, string message)
        {
            Assert.AreEqual(0, c.r, 2, message);
            Assert.AreEqual(255, c.b, 2, message);
        }

        private const int Left = Size / 4, Right = Size * 3 / 4, Mid = Size / 2;

        // ── 開始・プレビュー ──

        [Test]
        public void Start_InjectsPreviewAndEndRestoresTheMaterial()
        {
            Assert.IsNull(_material.GetTexture("_MainTex"));
            Assert.IsFalse(_material.IsKeywordEnabled("_NORMALMAP"));

            var s = Start();
            Assert.IsTrue(s.Injector.Active);
            Assert.AreEqual("_MainTex", s.Injector.BaseProperty);
            Assert.AreEqual("_BumpMap", s.Injector.NormalProperty);
            Assert.AreSame(s.Renderer.PreviewBase, _material.GetTexture("_MainTex"), "preview RenderTexture is on the material");
            Assert.AreSame(s.Renderer.PreviewNormal, _material.GetTexture("_BumpMap"));
            Assert.IsTrue(_material.IsKeywordEnabled("_NORMALMAP"), "normal map is enabled while previewing");
            Assert.IsTrue(PreviewRecoveryJournal.Exists, "journal is written before injecting");
            Assert.AreEqual(Size, s.Renderer.PreviewBase.width);

            SessionController.End();
            Assert.IsNull(SessionController.Current);
            Assert.IsNull(_material.GetTexture("_MainTex"), "original (empty) texture is back");
            Assert.IsNull(_material.GetTexture("_BumpMap"));
            Assert.IsFalse(_material.IsKeywordEnabled("_NORMALMAP"));
            Assert.IsFalse(PreviewRecoveryJournal.Exists, "journal is removed after a normal end");
        }

        [Test]
        public void SaveGuard_SuspendsAndResumesInjection()
        {
            var s = Start();
            PreviewGuards.SuspendForSave();
            Assert.IsNull(_material.GetTexture("_MainTex"), "nothing of the preview may be saved");
            Assert.IsTrue(s.Injector.Suspended);
            PreviewInjector.ResumeAll();
            Assert.AreSame(s.Renderer.PreviewBase, _material.GetTexture("_MainTex"));

            AssetDatabase.SaveAssets();
            string yaml = File.ReadAllText(TempDir + "/DtxTestMaterial.mat");
            StringAssert.DoesNotContain("PreviewBase", yaml);
        }

        [Test]
        public void Journal_RestoresTheOriginalTextureAfterAnAbnormalEnd()
        {
            // 元のテクスチャを持つマテリアル
            string texPath = TempDir + "/Original.png";
            File.WriteAllBytes(texPath, new Texture2D(4, 4).EncodeToPNG());
            AssetDatabase.ImportAsset(texPath);
            var original = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            _material.SetTexture("_MainTex", original);

            var s = Start();
            Assert.AreSame(original, s.Injector.OriginalBase);
            Assert.IsTrue(PreviewRecoveryJournal.Exists);

            // 異常終了を模す: 復元されないままプレビューの参照だけが消えた状態
            _material.SetTexture("_MainTex", null);
            _material.DisableKeyword("_NORMALMAP");
            _material.EnableKeyword("_NORMALMAP");
            int restored = PreviewRecoveryJournal.Recover();

            Assert.AreEqual(1, restored);
            Assert.AreSame(original, _material.GetTexture("_MainTex"));
            Assert.IsFalse(_material.IsKeywordEnabled("_NORMALMAP"), "keyword is back to its original state");
            Assert.IsFalse(PreviewRecoveryJournal.Exists);
            Assert.AreEqual(0, PreviewRecoveryJournal.Recover(), "nothing to do the second time");
        }

        [Test]
        public void ShaderProperties_AreDetectedAndCanBeOverridden()
        {
            Assert.AreEqual("_MainTex", ShaderPropertyResolver.AutoBase(_material));
            Assert.AreEqual("_BumpMap", ShaderPropertyResolver.AutoNormal(_material));
            var props = ShaderPropertyResolver.TextureProperties(_material);
            CollectionAssert.Contains(props, "_MainTex");
            CollectionAssert.Contains(props, "_BumpMap");
            CollectionAssert.Contains(props, "_OcclusionMap");
            Assert.IsFalse(ShaderPropertyResolver.IsTexture2D(_material, "_Color"));
            Assert.IsNull(ShaderPropertyResolver.AutoBase(null));

            var s = Start();
            SettingsService.Set(SettingKeys.BaseProperty, "_OcclusionMap");
            s.BeginPreview();
            Assert.AreEqual("_OcclusionMap", s.Injector.BaseProperty);
            Assert.IsNull(_material.GetTexture("_MainTex"), "the previous property is restored when switching");
            Assert.AreSame(s.Renderer.PreviewBase, _material.GetTexture("_OcclusionMap"));
            SettingsService.ResetToLower(SettingKeys.BaseProperty);
        }

        [Test]
        public void NewDocument_UsesTheMaterialTexturesAsBackground()
        {
            string texPath = TempDir + "/Base512.png";
            File.WriteAllBytes(texPath, new Texture2D(512, 512).EncodeToPNG());
            AssetDatabase.ImportAsset(texPath);
            var original = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            _material.SetTexture("_MainTex", original);

            Assert.IsTrue(SessionController.Start(_renderer));
            var doc = DocumentHost.instance.Doc;
            Assert.AreEqual(BackgroundBaseKind.Texture, doc.background.baseKind);
            Assert.AreEqual(AssetDatabase.AssetPathToGUID(texPath), doc.background.baseTexture.guid);
            Assert.AreEqual(512, doc.width, "resolution follows the existing texture");
            Assert.AreEqual(BackgroundNormalKind.Flat, doc.background.normalKind);
            Assert.AreEqual(SessionController.Current.Analysis.TriangleCount, doc.triangleCount);
        }

        // ── レイヤーと合成 ──

        [Test]
        public void SelectionLayer_PaintsOnlyTheSelectedTriangles()
        {
            var s = Start();
            AssertBlue(PreviewPixel(s, Left, Mid), "background before any layer");

            var layer = AddRedLayer(s, 0, 1);
            AssertRed(PreviewPixel(s, Left, Mid), "quad A is selected");
            AssertBlue(PreviewPixel(s, Right, Mid), "quad B is not");

            DocumentCommands.EditLayer(layer.id, "invert", l => l.mask.invert = true);
            s.Tick();
            AssertBlue(PreviewPixel(s, Left, Mid), "inverted");
            AssertRed(PreviewPixel(s, Right, Mid), "inverted");

            DocumentCommands.EditLayer(layer.id, "opacity", l => { l.mask.invert = false; l.mask.opacity = 0.5f; });
            s.Tick();
            var half = PreviewPixel(s, Left, Mid);
            Assert.AreEqual(128, half.r, 3);
            Assert.AreEqual(128, half.b, 3);

            DocumentCommands.EditLayer(layer.id, "hide", l => { l.mask.opacity = 1f; l.visible = false; });
            s.Tick();
            AssertBlue(PreviewPixel(s, Left, Mid), "hidden layer");
        }

        [Test]
        public void Feather_SoftensTheMaskEdge()
        {
            var s = Start();
            var layer = AddRedLayer(s, 0, 1);
            var hard = PreviewPixel(s, Mid + 2, Mid);
            AssertBlue(hard, "just outside the hard edge");

            DocumentCommands.EditLayer(layer.id, "feather", l => l.mask.feather = 6f / Size);
            s.Tick();
            var soft = PreviewPixel(s, Mid + 1, Mid);
            Assert.Greater(soft.r, 20, "the edge bleeds outward");
            Assert.Less(soft.r, 235);
            AssertRed(PreviewPixel(s, 4, Mid), "far inside stays solid");
            AssertBlue(PreviewPixel(s, Size - 4, Mid), "far outside stays clear");
        }

        [Test]
        public void ImageMaskLayer_UsesTheLuminanceOfTheImage()
        {
            // 上半分が白、下半分が黒のマスク画像
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var px = new Color32[64];
            for (int i = 0; i < 64; i++) px[i] = i >= 32 ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 255);
            tex.SetPixels32(px);
            string path = TempDir + "/Mask.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            var s = Start();
            LayerActions.AddImageMaskLayer(mask, MessageOrigin.Window);
            var layer = DocumentHost.instance.SelectedLayer;
            Assert.IsNotNull(layer);
            Assert.AreEqual(MaskSource.Image, layer.mask.source);
            Assert.AreEqual("Mask", layer.name);
            DocumentCommands.EditLayer(layer.id, "color", l => l.color = Color.red);
            s.Tick();

            AssertRed(PreviewPixel(s, Mid, Size - 8), "white part of the mask");
            AssertBlue(PreviewPixel(s, Mid, 8), "black part of the mask");
        }

        [Test]
        public void LayerOrderDuplicateDeleteAndUndo()
        {
            var s = Start();
            var red = AddRedLayer(s, 0, 1, 2, 3);
            var green = DocumentCommands.AddSelectionLayer("add", "Green", Bits(s, 0, 1), null);
            DocumentCommands.EditLayer(green.id, "color", l => l.color = Color.green);
            s.Tick();
            Assert.AreEqual(255, PreviewPixel(s, Left, Mid).g, 2, "green is on top");
            Assert.AreEqual(green.id, DocumentHost.instance.SelectedLayerId);

            DocumentCommands.Move("move", green.id, 0);
            s.Tick();
            AssertRed(PreviewPixel(s, Left, Mid), "red covers green after reordering");

            DocumentCommands.Duplicate("dup", red.id);
            Assert.AreEqual(3, DocumentHost.instance.Doc.layers.Count);
            Assert.AreEqual("Red 2", DocumentHost.instance.SelectedLayer.name);

            Undo.IncrementCurrentGroup();
            int before = DocumentHost.instance.Revision;
            DocumentCommands.Delete("delete", red.id);
            Assert.AreEqual(2, DocumentHost.instance.Doc.layers.Count);
            Undo.IncrementCurrentGroup();
            Undo.PerformUndo();
            Assert.AreEqual(3, DocumentHost.instance.Doc.layers.Count, "undo brings the layer back");
            Assert.Greater(DocumentHost.instance.Revision, before, "undo is reported as a change");
            Assert.IsNotNull(DocumentHost.instance.Doc.Find(red.id));
        }

        // ── 範囲選択のモード ──

        [Test]
        public void SelectNew_CreatesALayerFromTheWorkingSelection()
        {
            var s = Start();
            Assert.AreEqual(DtxMode.LayerEdit, DtxModeController.Mode);
            Assert.IsTrue(SessionCommands.IsPaused, "scene clicks are not captured while editing layers");

            DtxModeController.BeginSelectNew();
            Assert.AreEqual(DtxMode.SelectNew, DtxModeController.Mode);
            Assert.IsTrue(DtxModeController.IsSelecting);
            Assert.IsFalse(DtxModeController.Confirm(), "an empty selection is not turned into a layer");
            Assert.AreEqual(DtxMode.SelectNew, DtxModeController.Mode);
            Assert.AreEqual(0, DocumentHost.instance.Doc.layers.Count);

            EditCommands.ModifySelection("select", bits => { TriangleBits.Set(bits, 2, true); TriangleBits.Set(bits, 3, true); return true; });
            Assert.IsTrue(DtxModeController.Confirm());
            Assert.AreEqual(DtxMode.LayerEdit, DtxModeController.Mode);
            var doc = DocumentHost.instance.Doc;
            Assert.AreEqual(1, doc.layers.Count);
            Assert.IsTrue(TriangleBits.Get(doc.layers[0].mask.selectedBits, 2));
            Assert.IsFalse(TriangleBits.Get(doc.layers[0].mask.selectedBits, 0));
            Assert.AreEqual(doc.layers[0].id, DocumentHost.instance.SelectedLayerId);

            DocumentCommands.EditLayer(doc.layers[0].id, "color", l => l.color = Color.red);
            s.Tick();
            AssertRed(PreviewPixel(s, Right, Mid), "quad B was selected");
            AssertBlue(PreviewPixel(s, Left, Mid), "quad A was not");

            DtxModeController.BeginSelectNew();
            Assert.IsFalse(TriangleBits.AnySet(EditState.instance.SelectedBits), "a new selection starts empty");
            DtxModeController.Cancel();
            Assert.AreEqual(DtxMode.LayerEdit, DtxModeController.Mode);
            Assert.AreEqual(1, DocumentHost.instance.Doc.layers.Count);
        }

        [Test]
        public void Reselect_PreviewsLiveAndWritesBackOnlyOnConfirm()
        {
            var s = Start();
            var layer = AddRedLayer(s, 0, 1);

            DtxModeController.BeginReselect(layer.id);
            Assert.AreEqual(DtxMode.Reselect, DtxModeController.Mode);
            Assert.AreEqual(layer.id, DtxModeController.EditingLayerId);
            Assert.IsTrue(TriangleBits.Get(EditState.instance.SelectedBits, 0), "the layer's area is loaded into the working selection");

            // 四角形 A を外して B を選ぶ
            EditCommands.ModifySelection("select", bits =>
            {
                TriangleBits.Set(bits, 0, false);
                TriangleBits.Set(bits, 1, false);
                TriangleBits.Set(bits, 2, true);
                TriangleBits.Set(bits, 3, true);
                return true;
            });
            s.Tick();
            AssertRed(PreviewPixel(s, Right, Mid), "the model shows the new area before confirming");
            AssertBlue(PreviewPixel(s, Left, Mid), "the model shows the new area before confirming");
            Assert.IsTrue(TriangleBits.Get(DocumentHost.instance.Doc.Find(layer.id).mask.selectedBits, 0), "the document is not changed yet");

            DtxModeController.Cancel();
            s.Tick();
            AssertRed(PreviewPixel(s, Left, Mid), "cancel goes back to the stored area");
            AssertBlue(PreviewPixel(s, Right, Mid), "cancel goes back to the stored area");

            DtxModeController.BeginReselect(layer.id);
            EditCommands.ModifySelection("select", bits => { TriangleBits.Set(bits, 2, true); return true; });
            Assert.IsTrue(DtxModeController.Confirm());
            s.Tick();
            var stored = DocumentHost.instance.Doc.Find(layer.id).mask.selectedBits;
            Assert.IsTrue(TriangleBits.Get(stored, 0));
            Assert.IsTrue(TriangleBits.Get(stored, 2));
            Assert.IsFalse(TriangleBits.Get(stored, 3));
            Assert.AreEqual(DtxMode.LayerEdit, DtxModeController.Mode);
        }

        // ── 出力 ──

        [Test]
        public void Bake_ProducesBaseAndNormalAtTheOutputResolution()
        {
            var s = Start();
            var layer = AddRedLayer(s, 0, 1);
            DocumentCommands.EditLayer(layer.id, "flatten", l => l.replaceNormal = true);
            DocumentCommands.Edit("size", d => DocumentOps.SetSize(d, 128));

            var bake = TextureBaker.Bake(s, DocumentHost.instance.Doc);
            Assert.AreEqual(128, bake.Size);
            Assert.AreEqual(128 * 128 * 4, bake.BaseRgba.Length);
            Assert.AreEqual(128 * 128 * 4, bake.NormalRgba.Length);
            AssertRed(Pixel(bake.BaseRgba, 128, 32, 64), "left");
            AssertBlue(Pixel(bake.BaseRgba, 128, 96, 64), "right");
            var n = Pixel(bake.NormalRgba, 128, 32, 64);
            Assert.AreEqual(128, n.r, 1);
            Assert.AreEqual(128, n.g, 1);
            Assert.AreEqual(255, n.b, 1);
            Assert.AreEqual(255, n.a);
        }

        [Test]
        public void ExportPng_WritesFilesWithImportSettingsAndCanBeApplied()
        {
            // 元のテクスチャと同じフォルダ（テスト用の一時フォルダ）へ出力させる
            string originalPath = TempDir + "/Original.png";
            File.WriteAllBytes(originalPath, new Texture2D(4, 4).EncodeToPNG());
            AssetDatabase.ImportAsset(originalPath);
            _material.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(originalPath));

            var s = Start();
            AddRedLayer(s, 0, 1);
            SessionCommands.ExportPng(MessageOrigin.Window);

            string basePath = TempDir + "/DtxTestTarget_BaseMap.png";
            string normalPath = TempDir + "/DtxTestTarget_NormalMap.png";
            Assert.IsTrue(File.Exists(basePath), basePath);
            Assert.IsTrue(File.Exists(normalPath), normalPath);
            Assert.AreEqual(TextureImporterType.NormalMap, ((TextureImporter)AssetImporter.GetAtPath(normalPath)).textureType);
            var baseImporter = (TextureImporter)AssetImporter.GetAtPath(basePath);
            Assert.AreEqual(TextureImporterType.Default, baseImporter.textureType);
            Assert.IsTrue(baseImporter.sRGBTexture);
            Assert.IsTrue(SourceImage.TryLoadRgba(basePath, out byte[] rgba, out int w, out int h));
            Assert.AreEqual(Size, w);
            Assert.AreEqual(Size, h);
            AssertRed(Pixel(rgba, Size, Left, Mid), "exported PNG, left");
            AssertBlue(Pixel(rgba, Size, Right, Mid), "exported PNG, right");
            Assert.AreSame(s.Renderer.PreviewBase, _material.GetTexture("_MainTex"), "preview is back on the material after exporting");

            PngExporter.ApplyToMaterial(s, basePath, normalPath, MessageOrigin.Window);
            Assert.AreSame(s.Renderer.PreviewBase, _material.GetTexture("_MainTex"), "still previewing");
            SessionController.End();
            Assert.AreEqual(basePath, AssetDatabase.GetAssetPath(_material.GetTexture("_MainTex")), "the applied output stays after the session");
            Assert.AreEqual(normalPath, AssetDatabase.GetAssetPath(_material.GetTexture("_BumpMap")));
            Assert.IsTrue(_material.IsKeywordEnabled("_NORMALMAP"));
        }

        [Test]
        public void ExportPng_NeverOverwritesATextureTheDocumentReads()
        {
            // 下地に使っているテクスチャが、出力ファイルと同じ名前だった場合
            string clash = TempDir + "/DtxTestTarget_BaseMap.png";
            File.WriteAllBytes(clash, new Texture2D(4, 4).EncodeToPNG());
            AssetDatabase.ImportAsset(clash);
            _material.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(clash));
            Assert.IsTrue(SessionController.Start(_renderer));
            var s = SessionController.Current;
            DocumentCommands.Edit("size", d => DocumentOps.SetSize(d, Size));
            long before = new FileInfo(clash).Length;

            Assert.AreEqual(clash, AssetDatabase.GUIDToAssetPath(DocumentHost.instance.Doc.background.baseTexture.guid));
            Assert.AreEqual(TempDir + "/DtxTestTarget_BaseMap_2.png", OutputPaths.AvoidReferenced(clash, DocumentHost.instance.Doc));
            Assert.AreEqual(TempDir + "/Other.png", OutputPaths.AvoidReferenced(TempDir + "/Other.png", DocumentHost.instance.Doc));

            SessionCommands.ExportPng(MessageOrigin.Window);
            Assert.AreEqual(before, new FileInfo(clash).Length, "the background texture is left alone");
            Assert.IsTrue(File.Exists(TempDir + "/DtxTestTarget_BaseMap_2.png"));
        }

        [Test]
        public void Psd_ExportHasCleanStructureAndReloadRestoresTheDocument()
        {
            var s = Start();
            var layer = AddRedLayer(s, 0, 1);
            DocumentCommands.EditLayer(layer.id, "setup", l =>
            {
                l.name = "布 Cloth";
                l.blendMode = BlendMode.Multiply;
                l.opacity = 0.5f;
                l.replaceNormal = true;
            });
            var second = DocumentCommands.AddSelectionLayer("add", "Top", Bits(s, 2, 3), null);
            DocumentCommands.EditLayer(second.id, "hide", l => l.visible = false);
            var expected = DocumentCodec.Clone(DocumentHost.instance.Doc);

            Assert.IsTrue(PsdExporter.Export(s, _psdPath, MessageOrigin.Window));
            Assert.IsTrue(File.Exists(_psdPath));
            Assert.AreSame(s.Renderer.PreviewBase, _material.GetTexture("_MainTex"), "preview is back after exporting");

            // 構造: BaseMap フォルダ（表示）と NormalMap フォルダ（非表示）
            var psd = PsdReader.Read(File.ReadAllBytes(_psdPath), true);
            Assert.AreEqual(Size, psd.Width);
            var names = new System.Collections.Generic.List<string>();
            foreach (var l in psd.Layers) names.Add(l.SectionType == 3 ? "</>" : l.Name);
            CollectionAssert.AreEqual(new[] { "</>", "Background", "布 Cloth", "Top", "BaseMap", "</>", "Background", "布 Cloth", "NormalMap" }, names);
            Assert.IsTrue(psd.Layers[4].Visible);
            Assert.IsFalse(psd.Layers[8].Visible, "NormalMap folder is hidden");

            var cloth = psd.Layers[2];
            Assert.AreEqual("mul ", cloth.BlendKey);
            Assert.AreEqual(128, cloth.Opacity);
            Assert.IsTrue(cloth.Visible);
            Assert.IsFalse(psd.Layers[3].Visible, "hidden layer stays hidden in the PSD");
            // PSD は行 0 が上端。レイヤーの画素は全面の質感、マスクが範囲
            AssertRed(Pixel(cloth.Rgba, Size, Right, Mid), "layer pixels are the full-canvas fill");
            Assert.AreEqual(255, cloth.Mask[Mid * Size + Left], "mask covers quad A");
            Assert.AreEqual(0, cloth.Mask[Mid * Size + Right], "mask excludes quad B");
            AssertBlue(Pixel(psd.Layers[1].Rgba, Size, Left, Mid), "background layer");
            var flat = Pixel(psd.Layers[7].Rgba, Size, Left, Mid);
            Assert.AreEqual(128, flat.r, 1);
            Assert.AreEqual(255, flat.b, 1);
            // 統合画像 = BaseMap の合成結果（赤 × 青の乗算 50% → 青が残り赤が乗らない）
            Assert.AreEqual(0, psd.MergedRgb[(Mid * Size + Right) * 3], 2);
            Assert.AreEqual(255, psd.MergedRgb[(Mid * Size + Right) * 3 + 2], 2);

            // 読み込み: 別の内容にしてから読み直すと元に戻る
            DocumentCommands.Delete("delete", layer.id);
            DocumentCommands.EditBackground("bg", b => b.color = Color.green);
            Undo.IncrementCurrentGroup();
            Assert.IsTrue(PsdImporter.Import(s, _psdPath, MessageOrigin.Window));
            var loaded = DocumentHost.instance.Doc;
            CoreDocumentTests.AssertSame(expected, loaded);
            Assert.IsFalse(loaded.layers[0].mask.HasEmbedded, "the backup raster is dropped when the mesh matches");

            Undo.IncrementCurrentGroup();
            Undo.PerformUndo();
            Assert.AreEqual(Color.green, DocumentHost.instance.Doc.background.color, "loading a PSD can be undone");
        }

        [Test]
        public void Psd_ImportRejectsForeignFilesWithoutTouchingTheDocument()
        {
            var s = Start();
            AddRedLayer(s, 0, 1);
            var before = DocumentCodec.Clone(DocumentHost.instance.Doc);
            int revision = DocumentHost.instance.Revision;

            // DTXTool 以外が作った PSD
            using (var fs = new FileStream(_psdPath, FileMode.Create))
            using (var w = new PsdWriter(4, 4))
            {
                w.AddLayer("L", true, 1f, PsdWriter.BlendNormal, new byte[64], null, false);
                w.Write(fs, new byte[64], false);
            }
            Assert.IsFalse(PsdImporter.Import(s, _psdPath, MessageOrigin.Window));

            File.WriteAllBytes(_psdPath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 });
            Assert.IsFalse(PsdImporter.Import(s, _psdPath, MessageOrigin.Window));
            Assert.IsFalse(PsdImporter.Import(s, _psdPath + ".missing", MessageOrigin.Window));

            CoreDocumentTests.AssertSame(before, DocumentHost.instance.Doc);
            Assert.AreEqual(revision, DocumentHost.instance.Revision);
            Assert.AreEqual("status.psdNotDtx", PsdImporter.MessageKey(PsdReadError.NoDtxData));
            Assert.AreEqual("status.psdNotPsd", PsdImporter.MessageKey(PsdReadError.NotPsd));
            Assert.AreEqual("status.psdTooNew", PsdImporter.MessageKey(DocumentCodecError.TooNew));
            Assert.AreEqual("status.psdCorrupted", PsdImporter.MessageKey(DocumentCodecError.Corrupted));
        }

        [Test]
        public void Psd_ImportOnADifferentMeshFallsBackToTheEmbeddedMask()
        {
            var s = Start();
            var layer = AddRedLayer(s, 0, 1);

            // 別のメッシュで作られた PSD を模す（復元情報のメッシュ情報だけを変える）
            var foreign = DocumentCodec.Decode(PsdExporter.BuildPayload(s, DocumentHost.instance.Doc));
            Assert.IsTrue(foreign.layers[0].mask.HasEmbedded, "selection layers carry a backup raster in the PSD");
            foreign.meshHash += 1;
            using (var fs = new FileStream(_psdPath, FileMode.Create))
            using (var w = new PsdWriter(4, 4))
            {
                w.AddImageResource(PsdReader.DtxResourceId, PsdReader.DtxResourceName, DocumentCodec.Encode(foreign));
                w.Write(fs, new byte[64], false);
            }

            Assert.IsTrue(PsdImporter.Import(s, _psdPath, MessageOrigin.Window));
            var loaded = DocumentHost.instance.Doc.Find(layer.id);
            Assert.AreEqual(MaskSource.Embedded, loaded.mask.source);
            Assert.IsFalse(TriangleBits.AnySet(loaded.mask.selectedBits));
            s.Tick();
            AssertRed(PreviewPixel(s, Left, Mid), "the area is still there, as an image");
            AssertBlue(PreviewPixel(s, Right, Mid), "the area is still there, as an image");
        }

        // ── 保存と再開 ──

        [Test]
        public void AutoSave_RestoresTheDocumentWhenTheSameTargetIsOpenedAgain()
        {
            var s = Start();
            var layer = AddRedLayer(s, 0, 1);
            string key = s.TargetKey;
            SessionController.End();
            Assert.IsTrue(File.Exists(DocumentAutoSave.FileFor(key)), "the document is saved when the session ends");

            // 別の対象の作業が挟まったことを模す（メモリ上のドキュメントを空にする）
            DocumentCommands.ResetForTarget(string.Empty, new DtxDocument());
            Assert.IsTrue(SessionController.Start(_renderer));
            var doc = DocumentHost.instance.Doc;
            Assert.AreEqual(1, doc.layers.Count);
            Assert.AreEqual(layer.id, doc.layers[0].id);
            Assert.IsTrue(TriangleBits.Get(doc.layers[0].mask.selectedBits, 1));
            Assert.AreEqual(Size, doc.width);
            SessionController.Current.Tick();
            AssertRed(PreviewPixel(SessionController.Current, Left, Mid), "restored layer is composited");
        }
    }
}
