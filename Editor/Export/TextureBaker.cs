using System;
using Dennoko.DTXTool.Core;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>ベイク結果。画素は RGBA32、行 0 = v 0（Unity のテクスチャと同じ向き）。</summary>
    internal sealed class BakeResult
    {
        public int Size;
        /// <summary>BaseMap（sRGB の値そのもの）。</summary>
        public byte[] BaseRgba;
        /// <summary>NormalMap（RGB = 法線、アルファ 255）。</summary>
        public byte[] NormalRgba;
    }

    /// <summary>ベイク用の資源の出どころ。マスクは出力解像度で作り、使い終わったらすぐ解放する（4096² を溜めない）。</summary>
    internal sealed class BakeSource : ICompositeSource
    {
        private readonly GpuResources _gpu;
        private readonly LayerMaskBuilder _masks;
        private readonly int _margin;

        public BakeSource(GpuResources gpu, LayerMaskBuilder masks, int margin)
        {
            _gpu = gpu;
            _masks = masks;
            _margin = margin;
        }

        public Texture MaskFor(Layer layer, int size) => _masks.Build(layer, size, _margin);

        public void DoneWithMask(Layer layer, Texture mask) => _gpu.Release(mask);

        public Texture2D Resolve(TextureRef reference) => TextureInfo.Resolve(reference);
    }

    /// <summary>
    /// 出力解像度での合成とエッジ拡張（Docs/Impl/ARCHITECTURE.md §8）。プレビューと同じ Compositor・同じシェーダーを使う。
    /// </summary>
    internal static class TextureBaker
    {
        /// <summary>エッジ拡張の幅（px）。設定が自動（負）なら解像度 / 16（2048px で 128px、LOD / Mipmap 対策）。</summary>
        public static int EffectivePadding(int size)
        {
            int p = SettingsService.Get(SettingKeys.EdgePadding);
            if (p < 0) p = size / 16;
            return Mathf.Clamp(p, 0, SettingKeys.MaxEdgePadding);
        }

        public static BakeResult Bake(WorkSession s, DtxDocument doc, bool bakeBase = true, bool bakeNormal = true)
        {
            int size = Mathf.Clamp(doc.width, 16, 8192);
            var gpu = s.Gpu;
            var masks = s.Renderer.Masks;
            var source = new BakeSource(gpu, masks, s.EffectiveMargin(new Vector2Int(size, size)));
            using (var compositor = new Compositor(gpu))
            {
                compositor.Render(doc, size, source);
                return Finish(s, compositor, size, bakeBase, bakeNormal);
            }
        }

        /// <summary>合成済みの Compositor からエッジ拡張 → 読み戻し。</summary>
        public static BakeResult Finish(WorkSession s, Compositor compositor, int size, bool bakeBase = true, bool bakeNormal = true)
        {
            var gpu = s.Gpu;
            int padding = EffectivePadding(size);
            RenderTexture coverage = null, paddedBase = null, paddedNormal = null, encoded = null;
            try
            {
                coverage = s.Renderer.Masks.RasterCoverage(size);
                byte[] baseRgba = null;
                byte[] normalRgba = null;
                if (bakeBase)
                {
                    paddedBase = Pad(gpu, compositor.BaseResult, coverage, padding, RenderTextureFormat.ARGB32);
                    baseRgba = GpuReadback.ReadRGBA32(paddedBase);
                }
                if (bakeNormal)
                {
                    paddedNormal = Pad(gpu, compositor.NormalResult, coverage, padding, RenderTextureFormat.ARGBHalf);
                    encoded = gpu.CreateRT("BakeNormal", size, size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear,
                        FilterMode.Point, TextureWrapMode.Clamp);
                    compositor.EncodeNormal(paddedNormal, encoded);   // 拡張した後で正規化して 8bit にする
                    normalRgba = GpuReadback.ReadRGBA32(encoded);
                }
                return new BakeResult
                {
                    Size = size,
                    BaseRgba = baseRgba,
                    NormalRgba = normalRgba,
                };
            }
            finally
            {
                gpu.Release(coverage);
                gpu.Release(paddedBase);
                gpu.Release(paddedNormal);
                gpu.Release(encoded);
            }
        }

        /// <summary>
        /// エッジ拡張（Voronoi Jump Flooding Dilation）。面が使っていない画素へ、最近傍の有効な画素の色を押し広げる。
        /// 戻り値は新しい RenderTexture（呼び出し側が解放する）。
        /// </summary>
        public static RenderTexture Pad(GpuResources gpu, RenderTexture color, RenderTexture coverage, int iterations, RenderTextureFormat format)
        {
            int width = color.width;
            int height = color.height;
            var mask = gpu.RequireMaterial("Hidden/DTXTool/LayerMask");
            var dst = gpu.CreateRT("PadResult", width, height, format, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            var prev = RenderTexture.active;
            try
            {
                if (iterations > 0)
                {
                    JfaDilate.DilateColor(gpu, mask, color, coverage, dst, width, height, iterations);
                }
                else
                {
                    Graphics.Blit(color, dst);
                }
                return dst;
            }
            catch (Exception)
            {
                gpu.Release(dst);
                throw;
            }
            finally
            {
                RenderTexture.active = prev;
                gpu.Release(mask);
            }
        }
    }
}
