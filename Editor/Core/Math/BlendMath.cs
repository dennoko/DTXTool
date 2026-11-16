using System;
using UnityEngine;

namespace Dennoko.DTXTool.Core
{
    /// <summary>
    /// 合成の式の CPU 実装。シェーダー（Compose.shader）と同じ式を書き、テストで両者が一致することを確かめる。
    /// 色はすべてガンマ空間（sRGB の値そのもの）で扱う。
    /// </summary>
    public static class BlendMath
    {
        /// <summary>1 チャンネルのブレンド結果（不透明度 1 の時の色）。d = 下、s = 上。</summary>
        public static float Blend(BlendMode mode, float d, float s)
        {
            switch (mode)
            {
                case BlendMode.Multiply: return d * s;
                case BlendMode.Additive: return Math.Min(d + s, 1f);
                case BlendMode.Screen: return 1f - (1f - d) * (1f - s);
                case BlendMode.Overlay: return d < 0.5f ? 2f * d * s : 1f - 2f * (1f - d) * (1f - s);
                default: return s;
            }
        }

        /// <summary>out.rgb = lerp(dst, Blend(dst, src), a)、out.a = dst.a（下地のアルファを保つ）。</summary>
        public static Color Composite(BlendMode mode, Color dst, Color src, float a)
        {
            a = Mathf.Clamp01(a);
            return new Color(
                Lerp(dst.r, Blend(mode, dst.r, src.r), a),
                Lerp(dst.g, Blend(mode, dst.g, src.g), a),
                Lerp(dst.b, Blend(mode, dst.b, src.b), a),
                dst.a);
        }

        /// <summary>レイヤーの寄与 a = マスク × レイヤー不透明度 × 質感のアルファ。</summary>
        public static float Coverage(float mask, float layerOpacity, float sourceAlpha)
            => Mathf.Clamp01(mask) * Mathf.Clamp01(layerOpacity) * Mathf.Clamp01(sourceAlpha);

        /// <summary>マスクの仕上げ（反転 → 不透明度）。</summary>
        public static float FinishMask(float raw, bool invert, float opacity)
            => (invert ? 1f - raw : raw) * Mathf.Clamp01(opacity);

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        // ── 色空間（sRGB の正確な式。シェーダーも同じ式を使う） ──

        public static float LinearToGamma(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x <= 0.0031308f ? 12.92f * x : 1.055f * (float)Math.Pow(x, 1.0 / 2.4) - 0.055f;
        }

        public static float GammaToLinear(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x <= 0.04045f ? x / 12.92f : (float)Math.Pow((x + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>ノーマル合成の式（接空間、z が面の外向き）。</summary>
    public static class NormalMath
    {
        public static readonly Vector3 Flat = new Vector3(0f, 0f, 1f);

        /// <summary>Reoriented Normal Mapping。平坦な detail を重ねても baseN は変わらない。</summary>
        public static Vector3 Rnm(Vector3 baseN, Vector3 detail)
        {
            var t = new Vector3(baseN.x, baseN.y, baseN.z + 1f);
            var u = new Vector3(-detail.x, -detail.y, detail.z);
            float k = Vector3.Dot(t, u) / Math.Max(t.z, 1e-5f);
            return new Vector3(t.x * k - u.x, t.y * k - u.y, t.z * k - u.z);
        }

        /// <summary>ノーマルマップのレイヤー: normalize(lerp(base, RNM(base, layer), a))。</summary>
        public static Vector3 BlendTexture(Vector3 baseN, Vector3 layerN, float a)
            => SafeNormalize(Vector3.Lerp(baseN, Rnm(baseN, layerN), Mathf.Clamp01(a)));

        /// <summary>平坦化のレイヤー: normalize(lerp(base, (0,0,1), a))。</summary>
        public static Vector3 BlendFlatten(Vector3 baseN, float a)
            => SafeNormalize(Vector3.Lerp(baseN, Flat, Mathf.Clamp01(a)));

        /// <summary>強さ（xy に掛ける）と UV の回転（度）を法線に反映する。</summary>
        public static Vector3 Prepare(Vector3 n, float scale, float rotationDegrees)
        {
            float x = n.x * scale, y = n.y * scale;
            if (rotationDegrees != 0f)
            {
                float rad = rotationDegrees * Mathf.Deg2Rad;
                float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
                float rx = c * x - s * y, ry = s * x + c * y;
                x = rx; y = ry;
            }
            return SafeNormalize(new Vector3(x, y, n.z));
        }

        public static Vector3 SafeNormalize(Vector3 v)
        {
            float len = (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
            return len > 1e-6f ? new Vector3(v.x / len, v.y / len, v.z / len) : Flat;
        }

        /// <summary>RGB（0..1）→ 法線。</summary>
        public static Vector3 Decode(float r, float g, float b)
            => SafeNormalize(new Vector3(r * 2f - 1f, g * 2f - 1f, b * 2f - 1f));

        /// <summary>法線 → RGB（0..1）。</summary>
        public static Vector3 Encode(Vector3 n)
            => new Vector3(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f);
    }

    /// <summary>レイヤーの UV 変形（シェーダーと同じ式）。</summary>
    public static class UvMath
    {
        /// <summary>タイリング: uv' = Rot(rotation)·((uv − 0.5) × tiling) + 0.5 + offset。</summary>
        public static Vector2 Tiling(Vector2 uv, UvTransform t)
        {
            float x = (uv.x - 0.5f) * t.tiling.x, y = (uv.y - 0.5f) * t.tiling.y;
            Rotate(ref x, ref y, t.rotation);
            return new Vector2(x + 0.5f + t.offset.x, y + 0.5f + t.offset.y);
        }

        /// <summary>デカール: uv' = Rot(−rotation)·(uv − center) / size + 0.5。[0,1] の外は inside = false。</summary>
        public static Vector2 Decal(Vector2 uv, Vector2 center, Vector2 size, float rotation, out bool inside)
        {
            float x = uv.x - center.x, y = uv.y - center.y;
            Rotate(ref x, ref y, -rotation);
            float sx = Math.Abs(size.x) < 1e-6f ? 1e-6f : size.x;
            float sy = Math.Abs(size.y) < 1e-6f ? 1e-6f : size.y;
            var r = new Vector2(x / sx + 0.5f, y / sy + 0.5f);
            inside = r.x >= 0f && r.x <= 1f && r.y >= 0f && r.y <= 1f;
            return r;
        }

        /// <summary>
        /// シェーダーへ渡す形。どちらの配置も uv' = A·uv + T の一次変換になる。
        /// A = (m00, m01, m10, m11)、uv'.x = m00·u + m01·v + T.x、uv'.y = m10·u + m11·v + T.y。
        /// </summary>
        public static void Affine(Layer layer, bool forNormal, out Vector4 a, out Vector2 t)
        {
            if (layer.placementMode == PlacementMode.Decal)
            {
                float rad = -layer.decalRotation * Mathf.Deg2Rad;
                float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
                float sx = Math.Abs(layer.decalSize.x) < 1e-6f ? 1e-6f : layer.decalSize.x;
                float sy = Math.Abs(layer.decalSize.y) < 1e-6f ? 1e-6f : layer.decalSize.y;
                a = new Vector4(c / sx, -s / sx, s / sy, c / sy);
                t = new Vector2(
                    0.5f - (a.x * layer.decalCenter.x + a.y * layer.decalCenter.y),
                    0.5f - (a.z * layer.decalCenter.x + a.w * layer.decalCenter.y));
            }
            else
            {
                var x = forNormal ? layer.EffectiveNormalXform : layer.baseXform;
                float rad = x.rotation * Mathf.Deg2Rad;
                float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
                a = new Vector4(c * x.tiling.x, -s * x.tiling.y, s * x.tiling.x, c * x.tiling.y);
                t = new Vector2(
                    0.5f + x.offset.x - 0.5f * (a.x + a.y),
                    0.5f + x.offset.y - 0.5f * (a.z + a.w));
            }
        }

        public static Vector2 Apply(Vector4 a, Vector2 t, Vector2 uv)
            => new Vector2(a.x * uv.x + a.y * uv.y + t.x, a.z * uv.x + a.w * uv.y + t.y);

        /// <summary>
        /// 絵が表面上で回って見える角度（度）。ノーマルマップの xy も同じだけ回す。
        /// タイリングは参照座標を +θ 回すので絵は −θ、デカールは絵そのものを +θ 回す。
        /// </summary>
        public static float NormalRotation(Layer layer)
            => layer.placementMode == PlacementMode.Decal ? layer.decalRotation : -layer.EffectiveNormalXform.rotation;

        private static void Rotate(ref float x, ref float y, float degrees)
        {
            if (degrees == 0f) return;
            float rad = degrees * Mathf.Deg2Rad;
            float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
            float rx = c * x - s * y, ry = s * x + c * y;
            x = rx; y = ry;
        }
    }
}
