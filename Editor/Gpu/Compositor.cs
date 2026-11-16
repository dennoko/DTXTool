using System;
using Dennoko.DTXTool.Core;
using UnityEngine;

namespace Dennoko.DTXTool
{
    /// <summary>合成に必要な外部の資源（マスクとテクスチャ）の出どころ。プレビューとベイクで差し替える。</summary>
    internal interface ICompositeSource
    {
        /// <summary>レイヤーの仕上げ済みマスク。null ならそのレイヤーを飛ばす。</summary>
        Texture MaskFor(Layer layer, int size);
        /// <summary>MaskFor で受け取ったマスクを使い終わった（ベイクはここで解放する）。</summary>
        void DoneWithMask(Layer layer, Texture mask);
        Texture2D Resolve(TextureRef reference);
    }

    /// <summary>
    /// ドキュメントを下から順に合成する（Docs/Impl/ARCHITECTURE.md §6）。プレビューもベイクも同じこのクラスを、解像度だけ変えて使う。
    ///   Base   : ARGB32 にガンマ値のまま累積する（ブレンドはガンマ空間）
    ///   Normal : ARGBHalf にベクトルのまま累積する（RNM ＋ 正規化。8bit にするのは最後の 1 回）
    /// </summary>
    internal sealed class Compositor : IDisposable
    {
        private readonly GpuResources _gpu;
        private readonly Material _m;
        private RenderTexture _baseA, _baseB, _normalA, _normalB;
        private int _size;

        public Compositor(GpuResources gpu)
        {
            _gpu = gpu;
            _m = gpu.RequireMaterial("Hidden/DTXTool/Compose");
        }

        /// <summary>BaseMap の合成結果（ガンマ値）。</summary>
        public RenderTexture BaseResult => _baseA;
        /// <summary>NormalMap の合成結果（正規化済みのベクトル）。</summary>
        public RenderTexture NormalResult => _normalA;
        public int Size => _size;

        public void Render(DtxDocument doc, int size, ICompositeSource source)
        {
            EnsureTargets(size);
            var prev = RenderTexture.active;
            try
            {
                InitBase(doc.background, source, _baseA);
                InitNormal(doc.background, source, _normalA);

                foreach (var layer in doc.layers)
                {
                    if (!DocumentOps.Contributes(layer)) continue;
                    bool hasBase = HasBase(layer, source, out var baseTex);
                    bool hasNormal = HasNormal(layer, source, out var normalTex);
                    if (!hasBase && !hasNormal) continue;

                    var mask = source.MaskFor(layer, size);
                    if (mask == null) continue;
                    try
                    {
                        _m.SetTexture(DtxShaderIds.MaskTex, mask);
                        _m.SetFloat(DtxShaderIds.Opacity, Mathf.Clamp01(layer.opacity));
                        if (hasBase)
                        {
                            SetBaseParams(layer, baseTex);
                            _m.SetTexture(DtxShaderIds.DstTex, _baseA);
                            Graphics.Blit(_gpu.Dummy, _baseB, _m, DtxShaderIds.BlendBase);
                            (_baseA, _baseB) = (_baseB, _baseA);
                        }
                        if (hasNormal)
                        {
                            SetNormalParams(layer, normalTex);
                            _m.SetTexture(DtxShaderIds.DstTex, _normalA);
                            Graphics.Blit(_gpu.Dummy, _normalB, _m, DtxShaderIds.BlendNormal);
                            (_normalA, _normalB) = (_normalB, _normalA);
                        }
                    }
                    finally
                    {
                        source.DoneWithMask(layer, mask);
                    }
                }
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        /// <summary>下地だけを描く（PSD の Background レイヤー用）。</summary>
        public void RenderBackground(DtxDocument doc, int size, ICompositeSource source)
        {
            EnsureTargets(size);
            var prev = RenderTexture.active;
            InitBase(doc.background, source, _baseA);
            InitNormal(doc.background, source, _normalA);
            RenderTexture.active = prev;
        }

        /// <summary>マテリアルへ渡す形にする。baseDst は sRGB、normalDst はリニアの RenderTexture。</summary>
        public void Present(RenderTexture baseDst, RenderTexture normalDst)
        {
            var prev = RenderTexture.active;
            if (baseDst != null)
            {
                _m.SetTexture(DtxShaderIds.SrcTex, _baseA);
                _m.SetFloat(DtxShaderIds.ToLinear, QualitySettings.activeColorSpace == ColorSpace.Linear ? 1f : 0f);
                Graphics.Blit(_gpu.Dummy, baseDst, _m, DtxShaderIds.PresentBase);
            }
            if (normalDst != null) EncodeNormal(_normalA, normalDst);
            RenderTexture.active = prev;
        }

        /// <summary>ベクトルの NormalMap を RGB（8bit）へ。</summary>
        public void EncodeNormal(Texture vectors, RenderTexture dst)
        {
            var prev = RenderTexture.active;
            _m.SetTexture(DtxShaderIds.SrcTex, vectors);
            Graphics.Blit(_gpu.Dummy, dst, _m, DtxShaderIds.PresentNormal);
            RenderTexture.active = prev;
        }

        /// <summary>PSD 用: レイヤーの質感を全面に描く（ストレートアルファ、ガンマ値。マスク・不透明度は掛けない）。false = 描くものが無い。</summary>
        public bool RasterBase(Layer layer, ICompositeSource source, RenderTexture dst)
        {
            if (!HasBase(layer, source, out var tex)) return false;
            var prev = RenderTexture.active;
            SetBaseParams(layer, tex);
            Graphics.Blit(_gpu.Dummy, dst, _m, DtxShaderIds.RasterBase);
            RenderTexture.active = prev;
            return true;
        }

        /// <summary>PSD 用: レイヤーの法線を RGB で全面に描く。false = ノーマルを持たないレイヤー。</summary>
        public bool RasterNormal(Layer layer, ICompositeSource source, RenderTexture dst)
        {
            if (!HasNormal(layer, source, out var tex)) return false;
            var prev = RenderTexture.active;
            SetNormalParams(layer, tex);
            Graphics.Blit(_gpu.Dummy, dst, _m, DtxShaderIds.RasterNormal);
            RenderTexture.active = prev;
            return true;
        }

        /// <summary>テクスチャが無ければ色だけを塗る。テクスチャ指定なのに参照が切れているレイヤーは、ベースへ何も描かない。</summary>
        private static bool HasBase(Layer layer, ICompositeSource source, out Texture2D tex)
        {
            tex = null;
            if (!layer.HasBaseTexture) return true;
            tex = source.Resolve(layer.baseTexture);
            return tex != null;
        }

        /// <summary>
        /// テクスチャがあれば重ねる / 置き換える、無ければ「下地の凹凸を消す」の時だけ平坦化する。
        /// テクスチャ指定なのに参照が切れているレイヤーは、ノーマルへ何も描かない。
        /// </summary>
        private static bool HasNormal(Layer layer, ICompositeSource source, out Texture2D tex)
        {
            tex = null;
            if (!layer.HasNormalTexture) return layer.replaceNormal;
            tex = source.Resolve(layer.normalTexture);
            return tex != null;
        }

        private void InitBase(Background bg, ICompositeSource source, RenderTexture dst)
        {
            var tex = bg.baseKind == BackgroundBaseKind.Texture ? source.Resolve(bg.baseTexture) : null;
            if (tex != null)
            {
                _m.SetTexture(DtxShaderIds.SrcTex, tex);
                _m.SetFloat(DtxShaderIds.SrcSRGB, TextureInfo.SampledAsLinearized(tex) ? 1f : 0f);
                Graphics.Blit(_gpu.Dummy, dst, _m, DtxShaderIds.InitBaseTex);
            }
            else
            {
                // 色はガンマ値のまま渡す（SetColor はリニアへ変換してしまうので SetVector を使う）
                _m.SetVector(DtxShaderIds.Color, ToVector(bg.color));
                Graphics.Blit(_gpu.Dummy, dst, _m, DtxShaderIds.InitBaseColor);
            }
        }

        private void InitNormal(Background bg, ICompositeSource source, RenderTexture dst)
        {
            var tex = bg.normalKind == BackgroundNormalKind.Texture ? source.Resolve(bg.normalTexture) : null;
            if (tex != null)
            {
                _m.SetTexture(DtxShaderIds.SrcTex, tex);
                _m.SetFloat(DtxShaderIds.SrcSRGB, TextureInfo.SampledAsLinearized(tex) ? 1f : 0f);
                _m.SetFloat(DtxShaderIds.NormalPacked, TextureInfo.IsPackedNormal(tex) ? 1f : 0f);
                Graphics.Blit(_gpu.Dummy, dst, _m, DtxShaderIds.InitNormalTex);
            }
            else
            {
                Graphics.Blit(_gpu.Dummy, dst, _m, DtxShaderIds.InitNormalFlat);
            }
        }

        private void SetBaseParams(Layer layer, Texture2D tex)
        {
            bool useTex = tex != null;
            _m.SetFloat(DtxShaderIds.UseTex, useTex ? 1f : 0f);
            _m.SetVector(DtxShaderIds.Color, ToVector(layer.color));
            _m.SetTexture(DtxShaderIds.SrcTex, useTex ? (Texture)tex : Texture2D.whiteTexture);
            _m.SetFloat(DtxShaderIds.SrcSRGB, useTex && TextureInfo.SampledAsLinearized(tex) ? 1f : 0f);
            _m.SetFloat(DtxShaderIds.BlendMode, (float)layer.blendMode);
            SetPlacement(layer, false, useTex);
        }

        private void SetNormalParams(Layer layer, Texture2D tex)
        {
            bool useTex = tex != null;
            _m.SetFloat(DtxShaderIds.NormalMode, !useTex ? 1f : layer.replaceNormal ? 3f : 2f);
            _m.SetTexture(DtxShaderIds.SrcTex, useTex ? (Texture)tex : Texture2D.normalTexture);
            _m.SetFloat(DtxShaderIds.SrcSRGB, useTex && TextureInfo.SampledAsLinearized(tex) ? 1f : 0f);
            _m.SetFloat(DtxShaderIds.NormalPacked, useTex && TextureInfo.IsPackedNormal(tex) ? 1f : 0f);
            _m.SetFloat(DtxShaderIds.NormalScale, layer.normalScale);
            float rad = UvMath.NormalRotation(layer) * Mathf.Deg2Rad;
            _m.SetVector(DtxShaderIds.NormalRot, new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), 0f, 0f));
            SetPlacement(layer, true, useTex);
        }

        /// <summary>デカールの範囲はテクスチャを使う時だけ意味を持つ（単色・平坦化はマスク全体に効く）。</summary>
        private void SetPlacement(Layer layer, bool forNormal, bool useTex)
        {
            UvMath.Affine(layer, forNormal, out var a, out var t);
            _m.SetVector(DtxShaderIds.UvA, a);
            _m.SetVector(DtxShaderIds.UvT, new Vector4(t.x, t.y, 0f, 0f));
            _m.SetFloat(DtxShaderIds.Decal, useTex && layer.placementMode == PlacementMode.Decal ? 1f : 0f);
        }

        private static Vector4 ToVector(Color c) => new Vector4(c.r, c.g, c.b, c.a);

        private void EnsureTargets(int size)
        {
            bool lost = _baseA == null || !_baseA.IsCreated() || !_baseB.IsCreated() || !_normalA.IsCreated() || !_normalB.IsCreated();
            if (!lost && size == _size) return;
            ReleaseTargets();
            _size = size;
            _baseA = _gpu.CreateRT("BaseA", size, size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            _baseB = _gpu.CreateRT("BaseB", size, size, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            _normalA = _gpu.CreateRT("NormalA", size, size, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
            _normalB = _gpu.CreateRT("NormalB", size, size, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, FilterMode.Point, TextureWrapMode.Clamp);
        }

        /// <summary>累積用の RenderTexture を手放す（ベイク後に 4096² を持ち続けない）。</summary>
        public void ReleaseTargets()
        {
            _gpu.Release(_baseA);
            _gpu.Release(_baseB);
            _gpu.Release(_normalA);
            _gpu.Release(_normalB);
            _baseA = _baseB = _normalA = _normalB = null;
            _size = 0;
        }

        public void Dispose()
        {
            ReleaseTargets();
            _gpu.Release(_m);
        }
    }
}
