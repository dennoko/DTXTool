using System;
using Dennoko.DTXTool.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Dennoko.DTXTool
{
    internal static class DtxShaderIds
    {
        public static readonly int DstTex = Shader.PropertyToID("_DstTex");
        public static readonly int MaskTex = Shader.PropertyToID("_MaskTex");
        public static readonly int SrcTex = Shader.PropertyToID("_SrcTex");
        public static readonly int NormalMaskTex = Shader.PropertyToID("_NormalMaskTex");
        public static readonly int NormalMaskSRGB = Shader.PropertyToID("_NormalMaskSRGB");
        public static readonly int ValidTex = Shader.PropertyToID("_ValidTex");
        public static readonly int Color = Shader.PropertyToID("_Color");
        public static readonly int UseTex = Shader.PropertyToID("_UseTex");
        public static readonly int SrcSRGB = Shader.PropertyToID("_SrcSRGB");
        public static readonly int Opacity = Shader.PropertyToID("_Opacity");
        public static readonly int BlendMode = Shader.PropertyToID("_BlendMode");
        public static readonly int UvA = Shader.PropertyToID("_UvA");
        public static readonly int UvT = Shader.PropertyToID("_UvT");
        public static readonly int Decal = Shader.PropertyToID("_Decal");
        public static readonly int NormalMode = Shader.PropertyToID("_NormalMode");
        public static readonly int NormalPacked = Shader.PropertyToID("_NormalPacked");
        public static readonly int NormalScale = Shader.PropertyToID("_NormalScale");
        public static readonly int NormalRot = Shader.PropertyToID("_NormalRot");
        public static readonly int ToLinear = Shader.PropertyToID("_ToLinear");
        public static readonly int OutSize = Shader.PropertyToID("_OutSize");
        public static readonly int Radius = Shader.PropertyToID("_Radius");
        public static readonly int Invert = Shader.PropertyToID("_Invert");

        // Compose.shader のパス番号
        public const int InitBaseColor = 0, InitBaseTex = 1, BlendBase = 2, InitNormalFlat = 3, InitNormalTex = 4,
            BlendNormal = 5, PresentBase = 6, PresentNormal = 7, RasterBase = 8, RasterNormal = 9;

        // LayerMask.shader のパス番号
        public const int ImportImage = 0, CopyRaw = 1, BlurH = 2, BlurV = 3, Finish = 4, PadColor = 5, PadValid = 6,
            JfaInit = 7, JfaStep = 8, JfaApplyColor = 9, JfaApplyMask = 10;
    }

    /// <summary>
    /// Voronoi Jump Flooding Algorithm (JFA+1) による高速 UV パディング。
    /// すべての UV 島を同時にシードとするため、大きなパディング幅でも近隣の別アイランドへ滲まない。
    /// パス数は O(log R) なので 128–256px のパディングも数パスで完了する。
    /// </summary>
    internal static class JfaDilate
    {
        private static readonly RenderTextureFormat SeedFormat =
            SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat)
                ? RenderTextureFormat.RGFloat
                : RenderTextureFormat.ARGBFloat;

        public static void DilateColor(GpuResources gpu, Material maskMat, Texture srcColor, Texture coverage, RenderTexture dst, int width, int height, int radius)
        {
            var prev = RenderTexture.active;
            var seeds = RunJfa(gpu, maskMat, coverage, Texture2D.blackTexture, width, height, radius);
            try
            {
                maskMat.SetVector(DtxShaderIds.OutSize, new Vector4(width, height, 1f / width, 1f / height));
                maskMat.SetFloat(DtxShaderIds.Radius, radius);
                maskMat.SetTexture(DtxShaderIds.SrcTex, srcColor);
                maskMat.SetTexture(DtxShaderIds.ValidTex, seeds);
                Graphics.Blit(gpu.Dummy, dst, maskMat, DtxShaderIds.JfaApplyColor);
            }
            finally
            {
                RenderTexture.active = prev;
                gpu.Release(seeds);
            }
        }

        public static void DilateMask(GpuResources gpu, Material maskMat, Texture merged, Texture coverage, RenderTexture dst, int width, int height, int radius)
        {
            var prev = RenderTexture.active;
            var seeds = RunJfa(gpu, maskMat, coverage, merged, width, height, radius);
            try
            {
                maskMat.SetVector(DtxShaderIds.OutSize, new Vector4(width, height, 1f / width, 1f / height));
                maskMat.SetFloat(DtxShaderIds.Radius, radius);
                maskMat.SetTexture(DtxShaderIds.SrcTex, merged);
                maskMat.SetTexture(DtxShaderIds.ValidTex, seeds);
                Graphics.Blit(gpu.Dummy, dst, maskMat, DtxShaderIds.JfaApplyMask);
            }
            finally
            {
                RenderTexture.active = prev;
                gpu.Release(seeds);
            }
        }

        private static RenderTexture RunJfa(GpuResources gpu, Material maskMat, Texture coverage, Texture extraSeeds, int width, int height, int radius)
        {
            var a = gpu.CreateRT("JfaA", width, height, SeedFormat, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            var b = gpu.CreateRT("JfaB", width, height, SeedFormat, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            try
            {
                maskMat.SetVector(DtxShaderIds.OutSize, new Vector4(width, height, 1f / width, 1f / height));
                maskMat.SetTexture(DtxShaderIds.ValidTex, coverage);
                maskMat.SetTexture(DtxShaderIds.SrcTex, extraSeeds);
                Graphics.Blit(gpu.Dummy, a, maskMat, DtxShaderIds.JfaInit);

                int maxDim = Mathf.Max(width, height);
                int target = Mathf.Min(Mathf.Max(1, radius), maxDim);
                int step = 1;
                while (step < target) step <<= 1;

                while (step >= 1)
                {
                    maskMat.SetFloat(DtxShaderIds.Radius, step);
                    maskMat.SetTexture(DtxShaderIds.SrcTex, a);
                    Graphics.Blit(gpu.Dummy, b, maskMat, DtxShaderIds.JfaStep);
                    (a, b) = (b, a);
                    step >>= 1;
                }
                // JFA+1: 最後に step=1 を 1 回追加して最近傍誤差を抑える
                maskMat.SetFloat(DtxShaderIds.Radius, 1f);
                maskMat.SetTexture(DtxShaderIds.SrcTex, a);
                Graphics.Blit(gpu.Dummy, b, maskMat, DtxShaderIds.JfaStep);
                (a, b) = (b, a);

                gpu.Release(b);
                b = null;
                return a;
            }
            catch (Exception)
            {
                gpu.Release(a);
                gpu.Release(b);
                throw;
            }
        }
    }

    /// <summary>テクスチャの読み方（色空間・ノーマルの格納形式）。</summary>
    internal static class TextureInfo
    {
        /// <summary>sRGB テクスチャをリニアのプロジェクトで読むと、シェーダーにはリニア値が届く。</summary>
        public static bool SampledAsLinearized(Texture tex)
            => tex != null && QualitySettings.activeColorSpace == ColorSpace.Linear && GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);

        /// <summary>Texture Type = Normal map でインポートされている（RG / AG 格納）。</summary>
        public static bool IsPackedNormal(Texture tex)
        {
            if (tex == null) return false;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return false;
            return AssetImporter.GetAtPath(path) is TextureImporter ti && ti.textureType == TextureImporterType.NormalMap;
        }

        public static Texture2D Resolve(TextureRef r)
        {
            if (r == null || r.IsEmpty) return null;
            string path = string.IsNullOrEmpty(r.guid) ? null : AssetDatabase.GUIDToAssetPath(r.guid);
            if (string.IsNullOrEmpty(path)) path = r.path;
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public static TextureRef ToRef(Texture tex)
        {
            var r = new TextureRef();
            if (tex == null) return r;
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return r;
            r.path = path;
            r.guid = AssetDatabase.AssetPathToGUID(path);
            return r;
        }
    }

    /// <summary>
    /// レイヤー 1 枚のマスクを作る（Docs/Impl/ARCHITECTURE.md §5.2）。
    ///   選択 → UV へラスタライズ → 余白の拡張 ┐
    ///   画像 → 輝度                           ├→ ぼかし → 反転 → 不透明度
    ///   埋め込みラスター                       ┘
    /// 作業中の選択（EditState）用の MaskPipeline とは別の RenderTexture を使う。三角形フラグのバッファだけは共有するので、
    /// 使い終わったら restoreFlags で元に戻す。
    /// </summary>
    internal sealed class LayerMaskBuilder
    {
        public const int MaxMargin = 512;

        private readonly GpuResources _gpu;
        private readonly TriMesh _tri;
        private readonly bool _wrap;
        private readonly Func<int[]> _active;
        private readonly Action _restoreFlags;
        private readonly Material _mask;
        private Texture2D _paintUpload;
        private byte[] _paintScratch;

        public LayerMaskBuilder(GpuResources gpu, TriMesh tri, bool needsWrap, Func<int[]> active, Action restoreFlags)
        {
            _gpu = gpu;
            _tri = tri;
            _wrap = needsWrap;
            _active = active;
            _restoreFlags = restoreFlags;
            _mask = gpu.RequireMaterial("Hidden/DTXTool/LayerMask");
        }

        /// <summary>仕上げ済みのマスク（呼び出し側が GpuResources.Release で解放する）。</summary>
        public RenderTexture Build(Layer layer, int size, int baseMargin)
        {
            var m = layer.mask;
            int feather = DocumentOps.FeatherPixels(m, size);
            RenderTexture raw;
            switch (m.source)
            {
                case MaskSource.Image:
                    raw = ImportImage(TextureInfo.Resolve(m.image), size);
                    break;
                case MaskSource.Embedded:
                    raw = ImportEmbedded(m, size);
                    break;
                default:
                    // ぼかしが UV シームで欠けないよう、ぼかす分だけ余白を広げておく
                    raw = RasterSelection(m.selectedBits, m.paintRle, size, Mathf.Min(MaxMargin, baseMargin + feather));
                    break;
            }
            try
            {
                return Finish(raw, size, feather, m.invert, m.opacity);
            }
            finally
            {
                _gpu.Release(raw);
            }
        }

        /// <summary>選択（＋手塗り）の 0 / 1 マスク。PSD に入れる控えのラスターにも使う。</summary>
        public RenderTexture RasterSelection(int[] bits, byte[] paintRle, int size, int margin)
        {
            var active = _active();
            var result = _gpu.CreateMaskRT("LayerMaskRaw", size, size, TextureWrapMode.Repeat);
            if (bits == null || bits.Length != active.Length)
            {
                // メッシュが変わって選択が使えない（空として扱う）
                using (new RenderTargetScope(result)) GL.Clear(false, true, Color.clear);
                return result;
            }

            var island = _gpu.CreateMaskRT("LayerIsland", size, size, TextureWrapMode.Clamp);
            var coverage = _gpu.CreateMaskRT("LayerCoverage", size, size, TextureWrapMode.Clamp);
            var merged = _gpu.CreateMaskRT("LayerMerged", size, size, TextureWrapMode.Clamp);
            try
            {
                _tri.UpdateFlags(bits, active, false);
                DrawUV(island, ShaderIds.RasterIsland);
                DrawUV(coverage, ShaderIds.RasterCoverage);

                var ops = _gpu.ImageOps;
                int n = Mathf.Clamp(Mathf.CeilToInt(PaintSurface.Size / (float)size), 1, 8);
                ops.SetVector(ShaderIds.OutSize, new Vector4(size, size, 1f / size, 1f / size));
                ops.SetTexture(ShaderIds.PaintTex, PaintTexture(paintRle));
                ops.SetTexture(ShaderIds.IslandTex, island);
                ops.SetFloat(ShaderIds.N, n);
                var prev = RenderTexture.active;
                Graphics.Blit(_gpu.Dummy, merged, ops, ShaderIds.OpDownsample);
                if (margin > 0)
                {
                    JfaDilate.DilateMask(_gpu, _mask, merged, coverage, result, size, size, margin);
                }
                else
                {
                    ops.SetTexture(ShaderIds.SrcTex, merged);
                    Graphics.Blit(_gpu.Dummy, result, ops, ShaderIds.OpCopy);
                }
                RenderTexture.active = prev;
            }
            finally
            {
                _gpu.Release(island);
                _gpu.Release(coverage);
                _gpu.Release(merged);
                _restoreFlags?.Invoke();
            }
            return result;
        }

        /// <summary>対象の面が使っている範囲（エッジ拡張の「有効な画素」）。</summary>
        public RenderTexture RasterCoverage(int size)
        {
            var coverage = _gpu.CreateMaskRT("BakeCoverage", size, size, TextureWrapMode.Clamp);
            try
            {
                _tri.UpdateFlags(new int[_active().Length], _active(), false);
                DrawUV(coverage, ShaderIds.RasterCoverage);
            }
            finally
            {
                _restoreFlags?.Invoke();
            }
            return coverage;
        }

        /// <summary>作業中の選択のマスク（MaskPipeline の出力）や取り込んだ画像を仕上げる。</summary>
        public RenderTexture Finish(Texture raw, int size, int featherPixels, bool invert, float opacity)
        {
            var result = _gpu.CreateRT("LayerMask", size, size, GpuResources.MaskFormat, RenderTextureReadWrite.Linear,
                FilterMode.Bilinear, TextureWrapMode.Repeat);
            _mask.SetVector(DtxShaderIds.OutSize, new Vector4(size, size, 1f / size, 1f / size));
            _mask.SetFloat(DtxShaderIds.Invert, invert ? 1f : 0f);
            _mask.SetFloat(DtxShaderIds.Opacity, Mathf.Clamp01(opacity));
            var prev = RenderTexture.active;
            if (featherPixels > 0)
            {
                var a = _gpu.CreateMaskRT("LayerBlurA", size, size, TextureWrapMode.Clamp);
                var b = _gpu.CreateMaskRT("LayerBlurB", size, size, TextureWrapMode.Clamp);
                // ぼかしは UV の端で反対側へ回り込ませない（端まで選んだ範囲の縁が欠けないように、端の値を延ばして読む）
                var rawWrap = raw.wrapMode;
                raw.wrapMode = TextureWrapMode.Clamp;
                try
                {
                    _mask.SetFloat(DtxShaderIds.Radius, featherPixels);
                    _mask.SetTexture(DtxShaderIds.SrcTex, raw);
                    Graphics.Blit(_gpu.Dummy, a, _mask, DtxShaderIds.BlurH);
                    _mask.SetTexture(DtxShaderIds.SrcTex, a);
                    Graphics.Blit(_gpu.Dummy, b, _mask, DtxShaderIds.BlurV);
                    _mask.SetTexture(DtxShaderIds.SrcTex, b);
                    Graphics.Blit(_gpu.Dummy, result, _mask, DtxShaderIds.Finish);
                }
                finally
                {
                    raw.wrapMode = rawWrap;
                    _gpu.Release(a);
                    _gpu.Release(b);
                }
            }
            else
            {
                _mask.SetTexture(DtxShaderIds.SrcTex, raw);
                Graphics.Blit(_gpu.Dummy, result, _mask, DtxShaderIds.Finish);
            }
            RenderTexture.active = prev;
            return result;
        }

        private RenderTexture ImportImage(Texture tex, int size)
        {
            var raw = _gpu.CreateMaskRT("LayerMaskImage", size, size, TextureWrapMode.Clamp);
            var prev = RenderTexture.active;
            if (tex == null)
            {
                // 参照切れ: 何も塗らない
                using (new RenderTargetScope(raw)) GL.Clear(false, true, Color.clear);
            }
            else
            {
                _mask.SetTexture(DtxShaderIds.SrcTex, tex);
                _mask.SetFloat(DtxShaderIds.SrcSRGB, TextureInfo.SampledAsLinearized(tex) ? 1f : 0f);
                Graphics.Blit(_gpu.Dummy, raw, _mask, DtxShaderIds.ImportImage);
            }
            RenderTexture.active = prev;
            return raw;
        }

        private RenderTexture ImportEmbedded(MaskSpec m, int size)
        {
            var raw = _gpu.CreateMaskRT("LayerMaskEmbedded", size, size, TextureWrapMode.Clamp);
            byte[] pixels = MaskRaster.Decompress(m.embedded, m.embeddedSize);
            var prev = RenderTexture.active;
            if (pixels == null)
            {
                using (new RenderTargetScope(raw)) GL.Clear(false, true, Color.clear);
            }
            else
            {
                var tex = _gpu.CreateTexture("EmbeddedUpload", m.embeddedSize, m.embeddedSize, TextureFormat.R8, true);
                try
                {
                    tex.filterMode = FilterMode.Bilinear;
                    tex.LoadRawTextureData(pixels);
                    tex.Apply(false, false);
                    _mask.SetTexture(DtxShaderIds.SrcTex, tex);
                    Graphics.Blit(_gpu.Dummy, raw, _mask, DtxShaderIds.CopyRaw);
                }
                finally
                {
                    _gpu.Release(tex);
                }
            }
            RenderTexture.active = prev;
            return raw;
        }

        private Texture PaintTexture(byte[] rle)
        {
            if (PaintRle.IsEmpty(rle)) return Texture2D.blackTexture;
            _paintScratch ??= new byte[PaintSurface.Size * PaintSurface.Size];
            if (!PaintRle.Decode(rle, _paintScratch)) return Texture2D.blackTexture;
            _paintUpload ??= _gpu.CreateTexture("LayerPaintUpload", PaintSurface.Size, PaintSurface.Size, TextureFormat.R8, true);
            _paintUpload.LoadRawTextureData(_paintScratch);
            _paintUpload.Apply(false, false);
            return _paintUpload;
        }

        private void DrawUV(RenderTexture target, int pass)
        {
            var m = _gpu.UVRaster;
            _tri.Bind(m);
            using (new RenderTargetScope(target))
            {
                GL.Clear(false, true, Color.clear);
                foreach (var offset in TriMesh.WrapOffsets(_wrap))
                {
                    m.SetVector(ShaderIds.UVOffset, offset);
                    m.SetPass(pass);
                    Graphics.DrawMeshNow(_tri.Mesh, Matrix4x4.identity);
                }
            }
        }
    }
}
