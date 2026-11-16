using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Dennoko.DTXTool
{
    /// <summary>BaseMap / NormalMap の PNG 出力（Docs/Impl/ARCHITECTURE.md §8）。</summary>
    internal static class PngExporter
    {
        public static void Export(WorkSession s, MessageOrigin origin)
        {
            if (s == null) return;
            var injector = s.Injector;
            try
            {
                EditorUtility.DisplayProgressBar("DTXTool", L10n.T("status.baking"), 0.3f);
                // ベイクと書き込みの間はプレビューを外しておく（保存処理と競合させない）
                injector.Suspend();
                var doc = DocumentHost.instance.Doc;
                var bake = TextureBaker.Bake(s, doc);

                EditorUtility.DisplayProgressBar("DTXTool", L10n.T("status.writing"), 0.8f);
                string folder = OutputPaths.ResolveFolder(s);
                string name = OutputPaths.BaseName(s);
                string basePath = OutputPaths.AvoidReferenced(OutputPaths.BasePng(folder, name), doc);
                string normalPath = OutputPaths.AvoidReferenced(OutputPaths.NormalPng(folder, name), doc);
                Write(basePath, bake.BaseRgba, bake.Size);
                Write(normalPath, bake.NormalRgba, bake.Size);

                DtxTexturePostprocessor.Register(basePath, DtxTexturePostprocessor.Kind.Base);
                DtxTexturePostprocessor.Register(normalPath, DtxTexturePostprocessor.Kind.Normal);
                AssetDatabase.ImportAsset(basePath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.ImportAsset(normalPath, ImportAssetOptions.ForceUpdate);
                DocumentAutoSave.FlushPending();

                var msg = new UserMessage
                {
                    Level = MessageLevel.Success,
                    Text = L10n.F("status.exported", Path.GetFileName(basePath), Path.GetFileName(normalPath)),
                    Origin = origin,
                    AssetPath = basePath,
                };
                if (injector.Active && !AlreadyApplied(injector, basePath, normalPath))
                {
                    msg.ActionLabel = L10n.T("term.apply");
                    msg.Action = _ => ApplyToMaterial(s, basePath, normalPath, origin);
                }
                Notifier.Post(msg);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                Debug.LogException(e);
                Notifier.Post(MessageLevel.Error, L10n.T("status.writeFailed"), origin);
            }
            finally
            {
                injector.Resume();
                EditorUtility.ClearProgressBar();
            }
        }

        internal static void Write(string path, byte[] rgba, int size)
        {
            var png = ImageConversion.EncodeArrayToPNG(rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)size, (uint)size);
            AtomicFile.WriteAllBytes(path, png);
        }

        private static bool AlreadyApplied(PreviewInjector injector, string basePath, string normalPath)
        {
            bool baseOk = injector.BaseProperty == null || AssetDatabase.GetAssetPath(injector.OriginalBase) == basePath;
            bool normalOk = injector.NormalProperty == null || AssetDatabase.GetAssetPath(injector.OriginalNormal) == normalPath;
            return baseOk && normalOk;
        }

        /// <summary>
        /// 出力したテクスチャを対象マテリアルへ割り当てる（Undo に記録する本物の変更）。
        /// プレビューを外した状態で記録するので、Undo の履歴にプレビュー用の RenderTexture は残らない。
        /// </summary>
        public static void ApplyToMaterial(WorkSession s, string basePath, string normalPath, MessageOrigin origin)
        {
            var injector = s?.Injector;
            if (injector == null || !injector.Active) return;
            var material = injector.Material;
            var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath);
            var normalTex = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);

            injector.Suspend();
            try
            {
                Undo.RecordObject(material, L10n.T("undo.applyOutput"));
                if (injector.BaseProperty != null && baseTex != null)
                {
                    material.SetTexture(injector.BaseProperty, baseTex);
                    injector.SetOriginal(injector.BaseProperty, baseTex);
                }
                if (injector.NormalProperty != null && normalTex != null)
                {
                    material.SetTexture(injector.NormalProperty, normalTex);
                    if (material.HasProperty("_UseBumpMap")) material.SetFloat("_UseBumpMap", 1f);
                    else material.EnableKeyword("_NORMALMAP");
                    injector.SetOriginal(injector.NormalProperty, normalTex);
                }
                EditorUtility.SetDirty(material);
            }
            finally
            {
                injector.Resume();
            }
            Notifier.Post(MessageLevel.Success, L10n.F("status.applied", material.name), origin);
        }
    }

    /// <summary>
    /// 本ツールが書いたパスの初回インポートだけに設定を当てる。
    /// 再出力（上書き）では当て直さないので、後からユーザーが変えた設定は保たれる。
    /// </summary>
    internal sealed class DtxTexturePostprocessor : AssetPostprocessor
    {
        public enum Kind { Base, Normal }

        private static readonly Dictionary<string, Kind> Pending = new Dictionary<string, Kind>();

        public static void Register(string path, Kind kind) => Pending[path] = kind;

        private void OnPreprocessTexture()
        {
            if (!Pending.TryGetValue(assetPath, out var kind)) return;
            Pending.Remove(assetPath);
            if (!assetImporter.importSettingsMissing) return;
            var ti = (TextureImporter)assetImporter;
            ti.mipmapEnabled = true;
            ti.streamingMipmaps = true;
            if (kind == Kind.Normal)
            {
                ti.textureType = TextureImporterType.NormalMap;
            }
            else
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = true;
                ti.alphaIsTransparency = false;
            }
        }
    }
}
