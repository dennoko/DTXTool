using System;
using System.IO;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>
    /// PSD の出力と読み込み（Docs/Impl/ARCHITECTURE.md §9）。
    ///   BaseMap フォルダ（表示）と NormalMap フォルダ（非表示）に、レイヤーをラスタライズして入れる。
    ///   復元情報は Image Resource ブロックに入れ、読み込みはそれだけを使う（画素のレイヤーは読まない）。
    /// </summary>
    internal static class PsdExporter
    {
        public const string BaseGroupName = "BaseMap";
        public const string NormalGroupName = "NormalMap";
        public const string BackgroundName = "Background";
        /// <summary>PSD に入れる控えのマスクラスターの一辺（メッシュが変わって選択を復元できない時の代わり）。</summary>
        public const int EmbeddedMaskSize = 1024;

        public static bool Export(WorkSession s, string filePath, MessageOrigin origin)
        {
            if (s == null || string.IsNullOrEmpty(filePath)) return false;
            var injector = s.Injector;
            string scratchPath = null;
            try
            {
                EditorUtility.DisplayProgressBar("DTXTool", L10n.T("status.psdWriting"), 0.1f);
                injector.Suspend();
                var doc = DocumentHost.instance.Doc;
                int size = Mathf.Clamp(doc.width, 16, 8192);

                scratchPath = FileUtil.GetUniqueTempPathInProject();
                using (var scratch = new FileStream(scratchPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                using (var writer = new PsdWriter(size, size, scratch))
                {
                    byte[] merged = WriteLayers(s, doc, size, writer);
                    writer.AddImageResource(PsdReader.DtxResourceId, PsdReader.DtxResourceName, BuildPayload(s, doc));

                    EditorUtility.DisplayProgressBar("DTXTool", L10n.T("status.psdWriting"), 0.9f);
                    string tmp = filePath + ".tmp";
                    using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        writer.Write(output, merged, true);
                    if (File.Exists(filePath)) File.Replace(tmp, filePath, null);
                    else File.Move(tmp, filePath);
                }

                DocumentAutoSave.FlushPending();
                string assetPath = ToAssetPath(filePath);
                if (assetPath != null) AssetDatabase.ImportAsset(assetPath);
                Notifier.Post(new UserMessage
                {
                    Level = MessageLevel.Success,
                    Text = L10n.F("status.psdExported", Path.GetFileName(filePath)),
                    Origin = origin,
                    AssetPath = assetPath,
                });
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException || e is ArgumentException)
            {
                Debug.LogException(e);
                Notifier.Post(MessageLevel.Error, L10n.F("status.psdWriteFailed", e.Message), origin);
                return false;
            }
            finally
            {
                injector.Resume();
                EditorUtility.ClearProgressBar();
                try
                {
                    if (scratchPath != null && File.Exists(scratchPath)) File.Delete(scratchPath);
                }
                catch (IOException) { }
            }
        }

        /// <summary>全レイヤーを writer に足し、統合画像（BaseMap の合成結果）を返す。</summary>
        private static byte[] WriteLayers(WorkSession s, DtxDocument doc, int size, PsdWriter writer)
        {
            var gpu = s.Gpu;
            var source = new BakeSource(gpu, s.Renderer.Masks, s.EffectiveMargin(new Vector2Int(size, size)));
            using (var compositor = new Compositor(gpu))
            {
                var raster = gpu.CreateRT("PsdRaster", size, size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear,
                    FilterMode.Point, TextureWrapMode.Clamp);
                try
                {
                    // 下地（Base / Normal）
                    compositor.RenderBackground(doc, size, source);
                    byte[] bgBase = GpuReadback.ReadRGBA32(compositor.BaseResult);
                    compositor.EncodeNormal(compositor.NormalResult, raster);
                    byte[] bgNormal = GpuReadback.ReadRGBA32(raster);

                    // BaseMap フォルダ（ファイル上は下から上の順）
                    writer.BeginGroup();
                    writer.AddLayer(BackgroundName, true, 1f, PsdWriter.BlendNormal, bgBase, null, true);
                    bgBase = null;
                    for (int i = 0; i < doc.layers.Count; i++)
                    {
                        var layer = doc.layers[i];
                        EditorUtility.DisplayProgressBar("DTXTool", L10n.T("status.psdWriting"), 0.1f + 0.35f * i / Mathf.Max(1, doc.layers.Count));
                        if (!compositor.RasterBase(layer, source, raster)) continue;
                        byte[] rgba = GpuReadback.ReadRGBA32(raster);
                        byte[] mask = ReadMask(source, gpu, layer, size);
                        writer.AddLayer(layer.name, layer.visible, layer.opacity, PsdWriter.BlendKey(layer.blendMode), rgba, mask, true);
                    }
                    writer.EndGroup(BaseGroupName, true);

                    // NormalMap フォルダ（初期非表示。外部ソフトでは「上で置き換える」近似表示になる）
                    writer.BeginGroup();
                    writer.AddLayer(BackgroundName, true, 1f, PsdWriter.BlendNormal, bgNormal, null, true);
                    bgNormal = null;
                    for (int i = 0; i < doc.layers.Count; i++)
                    {
                        var layer = doc.layers[i];
                        EditorUtility.DisplayProgressBar("DTXTool", L10n.T("status.psdWriting"), 0.45f + 0.35f * i / Mathf.Max(1, doc.layers.Count));
                        if (!compositor.RasterNormal(layer, source, raster)) continue;
                        byte[] rgba = GpuReadback.ReadRGBA32(raster);
                        byte[] mask = ReadMask(source, gpu, layer, size);
                        writer.AddLayer(layer.name, layer.visible, layer.opacity, PsdWriter.BlendNormal, rgba, mask, true);
                    }
                    writer.EndGroup(NormalGroupName, false, false);

                    // 統合画像 = BaseMap の合成結果（エッジ拡張込み。PNG 出力と同じもの）
                    compositor.Render(doc, size, source);
                    return TextureBaker.Finish(s, compositor, size).BaseRgba;
                }
                finally
                {
                    gpu.Release(raster);
                }
            }
        }

        private static byte[] ReadMask(BakeSource source, GpuResources gpu, Layer layer, int size)
        {
            var mask = source.MaskFor(layer, size);
            try
            {
                return GpuReadback.ReadR8((RenderTexture)mask);
            }
            finally
            {
                source.DoneWithMask(layer, mask);
            }
        }

        /// <summary>
        /// 復元情報。選択で作ったマスクには控えのラスターを付ける（メッシュが変わっても形だけは復元できるように）。
        /// 作業中のドキュメントは書き換えず、複製に対して行う。
        /// </summary>
        internal static byte[] BuildPayload(WorkSession s, DtxDocument doc)
        {
            var copy = DocumentCodec.Clone(doc);
            copy.meshHash = (long)s.Analysis.MeshHash;
            copy.triangleCount = s.Analysis.TriangleCount;
            copy.targetName = s.Target != null ? s.Target.name : string.Empty;
            int size = Mathf.Min(EmbeddedMaskSize, Mathf.Max(16, copy.width));
            int margin = s.EffectiveMargin(new Vector2Int(size, size));
            foreach (var layer in copy.layers)
            {
                var m = layer.mask;
                if (m.source != MaskSource.Selection) continue;
                var rt = s.Renderer.Masks.RasterSelection(m.selectedBits, m.paintRle, size, margin);
                try
                {
                    m.embedded = MaskRaster.Compress(GpuReadback.ReadR8(rt));
                    m.embeddedSize = size;
                }
                finally
                {
                    s.Gpu.Release(rt);
                }
            }
            return DocumentCodec.Encode(copy);
        }

        /// <summary>プロジェクトの Assets 以下ならアセットパス、そうでなければ null。</summary>
        internal static string ToAssetPath(string filePath)
        {
            string full = Path.GetFullPath(filePath).Replace('\\', '/');
            string data = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (!full.StartsWith(data + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + full.Substring(data.Length);
        }
    }

    internal static class PsdImporter
    {
        /// <summary>
        /// PSD を読み込んで続きから再開する。DTXTool で出力した PSD でなければ、現在のドキュメントに手を付けずに中断する。
        /// </summary>
        public static bool Import(WorkSession s, string filePath, MessageOrigin origin)
        {
            if (s == null || string.IsNullOrEmpty(filePath)) return false;
            DtxDocument doc;
            try
            {
                byte[] payload;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    payload = PsdReader.ReadDtxPayload(fs);
                doc = DocumentCodec.Decode(payload);
            }
            catch (PsdReadException e)
            {
                Notifier.Post(MessageLevel.Error, L10n.T(MessageKey(e.Error)), origin);
                return false;
            }
            catch (DocumentCodecException e)
            {
                Notifier.Post(MessageLevel.Error, L10n.T(MessageKey(e.Error)), origin);
                return false;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // ファイルが無い・開けないのは利用者に伝えれば足りる（例外としては記録しない）
                Debug.LogWarning("[DTXTool] " + e.Message);
                Notifier.Post(MessageLevel.Error, L10n.T("status.psdReadFailed"), origin);
                return false;
            }

            // ここまで全部通ってから差し替える
            int lost = DocumentOps.AdaptToMesh(doc, (long)s.Analysis.MeshHash, s.Analysis.TriangleCount);
            int missing = CountMissingTextures(doc);
            DtxModeController.Cancel();
            DocumentCommands.Replace(L10n.T("undo.loadPsd"), doc);

            string name = Path.GetFileName(filePath);
            if (lost > 0) Notifier.Post(MessageLevel.Warning, L10n.F("status.psdLoadedMeshChanged", name, lost), origin);
            else if (missing > 0) Notifier.Post(MessageLevel.Warning, L10n.F("status.psdLoadedMissing", name, missing), origin);
            else Notifier.Post(MessageLevel.Success, L10n.F("status.psdLoaded", name), origin);
            return true;
        }

        public static string MessageKey(PsdReadError error)
        {
            switch (error)
            {
                case PsdReadError.NotPsd: return "status.psdNotPsd";
                case PsdReadError.NoDtxData: return "status.psdNotDtx";
                default: return "status.psdCorrupted";
            }
        }

        public static string MessageKey(DocumentCodecError error)
        {
            switch (error)
            {
                case DocumentCodecError.NotDtx: return "status.psdNotDtx";
                case DocumentCodecError.TooNew: return "status.psdTooNew";
                default: return "status.psdCorrupted";
            }
        }

        /// <summary>参照が切れているテクスチャの数（読み込みは続け、警告だけ出す）。</summary>
        public static int CountMissingTextures(DtxDocument doc)
        {
            int n = 0;
            void Check(TextureRef r, bool used)
            {
                if (used && r != null && !r.IsEmpty && TextureInfo.Resolve(r) == null) n++;
            }
            Check(doc.background.baseTexture, doc.background.baseKind == BackgroundBaseKind.Texture);
            Check(doc.background.normalTexture, doc.background.normalKind == BackgroundNormalKind.Texture);
            foreach (var l in doc.layers)
            {
                Check(l.baseTexture, l.fillKind == FillKind.Texture);
                Check(l.normalTexture, l.normalKind == NormalKind.Texture);
                Check(l.mask.image, l.mask.source == MaskSource.Image);
            }
            return n;
        }
    }

    /// <summary>
    /// ドキュメントの自動保存（UserSettings/DTXTool/targets/{key}/document.bin）。
    /// 編集中の保険で、PSD の付属物ではない（PSD は単体で復元できる）。
    /// </summary>
    [InitializeOnLoad]
    internal static class DocumentAutoSave
    {
        private const string JobId = "document";

        static DocumentAutoSave()
        {
            DocumentHost.Changed += () => SaveScheduler.Schedule(JobId, 2.0, SaveNow);
        }

        public static string FileFor(string targetKey) => StoragePaths.TargetDir(targetKey) + "/document.bin";

        public static void FlushPending() => SaveScheduler.Flush(JobId);

        public static void SaveNow()
        {
            var host = DocumentHost.instance;
            if (string.IsNullOrEmpty(host.TargetKey)) return;
            try
            {
                AtomicFile.WriteAllBytes(FileFor(host.TargetKey), DocumentCodec.Encode(host.Doc));
            }
            catch (IOException e)
            {
                Debug.LogWarning("[DTXTool] " + e.Message);
            }
        }

        /// <summary>前回の作業。無い・読めない時は null。</summary>
        public static DtxDocument TryLoad(string targetKey)
        {
            string path = FileFor(targetKey);
            if (!File.Exists(path)) return null;
            try
            {
                return DocumentCodec.Decode(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is IOException || e is DocumentCodecException)
            {
                Debug.LogWarning("[DTXTool] " + e.Message);
                return null;
            }
        }
    }
}
