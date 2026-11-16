// レイヤーの合成（Graphics.Blit で使う全画面パス）。Docs/Impl/ARCHITECTURE.md §6
// 色はガンマ値のまま累積し、マテリアルへ渡す直前（Present）でだけプロジェクトの色空間に合わせる。
// 式は Core/Math/BlendMath.cs（CPU 実装）と同じにしてあり、テストで一致を確かめる。
Shader "Hidden/DTXTool/Compose"
{
    Properties
    {
        _MainTex ("Dummy", 2D) = "black" {}
    }

    CGINCLUDE
    #pragma target 4.5
    #include "UnityCG.cginc"

    sampler2D _DstTex;       // 下の累積結果
    sampler2D _MaskTex;      // レイヤーのマスク（.r）
    sampler2D _SrcTex;       // レイヤー / 下地のテクスチャ
    float4 _Color;           // 単色、または Tint（ガンマ値）
    float _UseTex;           // 1 = テクスチャ × Tint、0 = 単色
    float _SrcSRGB;          // 1 = sRGB テクスチャをリニアのプロジェクトで読んでいる（読んだ値をガンマ値へ戻す）
    float _Opacity;
    float _BlendMode;        // 0 Normal / 1 Multiply / 2 Additive / 3 Screen / 4 Overlay
    float4 _UvA;             // uv' = A·uv + T の A（m00, m01, m10, m11）
    float4 _UvT;             // T（xy）
    float _Decal;            // 1 = デカール（[0,1] の外は寄与 0）
    float _NormalMode;       // レイヤー: 1 = 平坦化 / 2 = ノーマルマップ
    float _NormalPacked;     // 1 = Normal map としてインポート済み（RG / AG 格納）
    float _NormalScale;
    float4 _NormalRot;       // (cos, sin)
    float _ToLinear;         // Present: 1 = リニアのプロジェクト

    float3 LinearToGammaExact(float3 c)
    {
        c = saturate(c);
        float3 lo = c * 12.92;
        float3 hi = 1.055 * pow(max(c, 1e-7), 1.0 / 2.4) - 0.055;
        return float3(c.r <= 0.0031308 ? lo.r : hi.r, c.g <= 0.0031308 ? lo.g : hi.g, c.b <= 0.0031308 ? lo.b : hi.b);
    }

    float3 GammaToLinearExact(float3 c)
    {
        c = saturate(c);
        float3 lo = c / 12.92;
        float3 hi = pow((c + 0.055) / 1.055, 2.4);
        return float3(c.r <= 0.04045 ? lo.r : hi.r, c.g <= 0.04045 ? lo.g : hi.g, c.b <= 0.04045 ? lo.b : hi.b);
    }

    float2 LayerUV(float2 uv)
    {
        return float2(dot(_UvA.xy, uv), dot(_UvA.zw, uv)) + _UvT.xy;
    }

    float Inside(float2 luv)
    {
        if (_Decal < 0.5) return 1.0;
        return (luv.x >= 0.0 && luv.x <= 1.0 && luv.y >= 0.0 && luv.y <= 1.0) ? 1.0 : 0.0;
    }

    // タイリングは frac で折り返す（Clamp のテクスチャでも繰り返す）。ミップは折り返す前の勾配で選ぶ
    float4 SampleSrc(float2 luv, float2 dx, float2 dy)
    {
        float2 uv = _Decal > 0.5 ? saturate(luv) : frac(luv);
        return tex2Dgrad(_SrcTex, uv, dx, dy);
    }

    float3 FixGamma(float3 rgb)
    {
        return _SrcSRGB > 0.5 ? LinearToGammaExact(rgb) : rgb;
    }

    float3 DecodeNormal(float4 p)
    {
        float3 n;
        if (_NormalPacked > 0.5)
        {
            // UnpackNormal（RG または AG 格納）と同じ
            p.x *= p.w;
            n.xy = p.xy * 2.0 - 1.0;
            n.z = sqrt(saturate(1.0 - dot(n.xy, n.xy)));
        }
        else
        {
            n = FixGamma(p.rgb) * 2.0 - 1.0;
        }
        float len = length(n);
        return len > 1e-5 ? n / len : float3(0.0, 0.0, 1.0);
    }

    float3 SafeNormalize(float3 n)
    {
        float len = length(n);
        return len > 1e-5 ? n / len : float3(0.0, 0.0, 1.0);
    }

    // レイヤーの法線（強さ・UV の回転を反映）
    float3 LayerNormal(float2 luv, float2 dx, float2 dy)
    {
        if (_NormalMode < 1.5) return float3(0.0, 0.0, 1.0);
        float3 n = DecodeNormal(SampleSrc(luv, dx, dy));
        float2 xy = n.xy * _NormalScale;
        xy = float2(_NormalRot.x * xy.x - _NormalRot.y * xy.y, _NormalRot.y * xy.x + _NormalRot.x * xy.y);
        return SafeNormalize(float3(xy, n.z));
    }

    float BlendChannel(float d, float s)
    {
        if (_BlendMode < 0.5) return s;
        if (_BlendMode < 1.5) return d * s;
        if (_BlendMode < 2.5) return min(d + s, 1.0);
        if (_BlendMode < 3.5) return 1.0 - (1.0 - d) * (1.0 - s);
        return d < 0.5 ? 2.0 * d * s : 1.0 - 2.0 * (1.0 - d) * (1.0 - s);
    }

    // レイヤーの質感（ストレートアルファ、ガンマ値）
    float4 LayerColor(float2 luv, float2 dx, float2 dy)
    {
        float4 src = _Color;
        if (_UseTex > 0.5)
        {
            float4 t = SampleSrc(luv, dx, dy);
            t.rgb = FixGamma(t.rgb);
            src = t * _Color;
        }
        return saturate(src);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off

        // 0: InitBaseColor（下地 = 単色）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target { return saturate(_Color); }
            ENDCG
        }

        // 1: InitBaseTex（下地 = 既存の BaseMap）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_SrcTex, i.uv);
                c.rgb = FixGamma(c.rgb);
                return saturate(c);
            }
            ENDCG
        }

        // 2: BlendBase（out.rgb = lerp(dst, Blend(dst, src), mask × opacity × src.a)、out.a = dst.a）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float4 dst = tex2D(_DstTex, i.uv);
                float2 luv = LayerUV(i.uv);
                float4 src = LayerColor(luv, ddx(luv), ddy(luv));
                float a = saturate(tex2D(_MaskTex, i.uv).r * _Opacity * src.a * Inside(luv));
                float3 b = float3(BlendChannel(dst.r, src.r), BlendChannel(dst.g, src.g), BlendChannel(dst.b, src.b));
                return float4(lerp(dst.rgb, b, a), dst.a);
            }
            ENDCG
        }

        // 3: InitNormalFlat（下地 = 平坦）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target { return float4(0.0, 0.0, 1.0, 1.0); }
            ENDCG
        }

        // 4: InitNormalTex（下地 = 既存の NormalMap。ベクトルのまま持つ）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                return float4(DecodeNormal(tex2D(_SrcTex, i.uv)), 1.0);
            }
            ENDCG
        }

        // 5: BlendNormal（ノーマルマップ = RNM、平坦化 = (0,0,1) への置き換え）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float3 baseN = SafeNormalize(tex2D(_DstTex, i.uv).xyz);
                float2 luv = LayerUV(i.uv);
                float2 dx = ddx(luv);
                float2 dy = ddy(luv);
                float a = saturate(tex2D(_MaskTex, i.uv).r * _Opacity * Inside(luv));
                float3 target;
                if (_NormalMode < 1.5)
                {
                    target = float3(0.0, 0.0, 1.0);
                }
                else
                {
                    float3 layerN = LayerNormal(luv, dx, dy);
                    float3 t = baseN + float3(0.0, 0.0, 1.0);
                    float3 u = layerN * float3(-1.0, -1.0, 1.0);
                    target = t * (dot(t, u) / max(t.z, 1e-5)) - u;
                }
                return float4(SafeNormalize(lerp(baseN, target, a)), 1.0);
            }
            ENDCG
        }

        // 6: PresentBase（累積のガンマ値 → マテリアル用の sRGB テクスチャ）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_SrcTex, i.uv);
                if (_ToLinear > 0.5) c.rgb = GammaToLinearExact(c.rgb);
                return c;
            }
            ENDCG
        }

        // 7: PresentNormal（ベクトル → RGB。アルファ 1 なので UnpackNormal でそのまま読める）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float3 n = SafeNormalize(tex2D(_SrcTex, i.uv).xyz);
                return float4(n * 0.5 + 0.5, 1.0);
            }
            ENDCG
        }

        // 8: RasterBase（PSD 用。レイヤーの質感を全面に描く。マスク・不透明度は掛けない）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float2 luv = LayerUV(i.uv);
                float4 src = LayerColor(luv, ddx(luv), ddy(luv));
                return float4(src.rgb, src.a * Inside(luv));
            }
            ENDCG
        }

        // 9: RasterNormal（PSD 用。レイヤーの法線を RGB で全面に描く）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float2 luv = LayerUV(i.uv);
                return float4(LayerNormal(luv, ddx(luv), ddy(luv)) * 0.5 + 0.5, Inside(luv));
            }
            ENDCG
        }
    }
}
