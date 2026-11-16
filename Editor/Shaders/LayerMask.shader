// レイヤーのマスクの取り込みと仕上げ（ぼかし → 反転 → 不透明度）。Docs/Impl/ARCHITECTURE.md §5.2
// エッジ拡張（UV シーム対策）のパスもここに置く。
Shader "Hidden/DTXTool/LayerMask"
{
    Properties
    {
        _MainTex ("Dummy", 2D) = "black" {}
    }

    CGINCLUDE
    #pragma target 4.5
    #include "UnityCG.cginc"

    sampler2D _SrcTex;
    sampler2D _ValidTex;
    float4 _OutSize;     // (w, h, 1/w, 1/h)
    float _SrcSRGB;      // 1 = sRGB テクスチャをリニアのプロジェクトで読んでいる
    float _Radius;       // ぼかしの半径（px）
    float _Invert;
    float _Opacity;

    float3 LinearToGammaExact(float3 c)
    {
        c = saturate(c);
        float3 lo = c * 12.92;
        float3 hi = 1.055 * pow(max(c, 1e-7), 1.0 / 2.4) - 0.055;
        return float3(c.r <= 0.0031308 ? lo.r : hi.r, c.g <= 0.0031308 ? lo.g : hi.g, c.b <= 0.0031308 ? lo.b : hi.b);
    }

    float4 Lod0(sampler2D tex, float2 uv)
    {
        return tex2Dlod(tex, float4(uv, 0, 0));
    }

    float Blur(float2 uv, float2 dir)
    {
        int r = (int)_Radius;
        if (r < 1) return Lod0(_SrcTex, uv).r;
        float sigma = max(_Radius / 2.5, 0.5);
        float sum = 0.0;
        float wsum = 0.0;
        [loop] for (int k = -r; k <= r; k++)
        {
            float w = exp(-(k * k) / (2.0 * sigma * sigma));
            sum += Lod0(_SrcTex, uv + dir * k).r * w;
            wsum += w;
        }
        return sum / wsum;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off

        // 0: ImportImage（マスク画像 → 輝度 × アルファ）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_SrcTex, i.uv);
                if (_SrcSRGB > 0.5) c.rgb = LinearToGammaExact(c.rgb);
                return saturate(dot(c.rgb, float3(0.299, 0.587, 0.114)) * c.a);
            }
            ENDCG
        }

        // 1: CopyRaw（.r をそのまま写す）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target { return tex2D(_SrcTex, i.uv).r; }
            ENDCG
        }

        // 2: BlurH
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target { return Blur(i.uv, float2(_OutSize.z, 0.0)); }
            ENDCG
        }

        // 3: BlurV
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target { return Blur(i.uv, float2(0.0, _OutSize.w)); }
            ENDCG
        }

        // 4: Finish（反転 → 不透明度）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float v = Lod0(_SrcTex, i.uv).r;
                if (_Invert > 0.5) v = 1.0 - v;
                return saturate(v * _Opacity);
            }
            ENDCG
        }

        // 5: PadColor（エッジ拡張の 1 歩。面が使っていない画素へ、隣の有効な画素の平均を入れる。アルファは変えない）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = Lod0(_SrcTex, i.uv);
                if (Lod0(_ValidTex, i.uv).r > 0.5) return c;
                float3 sum = 0.0;
                float n = 0.0;
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 uv = i.uv + float2(x, y) * _OutSize.zw;
                        float v = Lod0(_ValidTex, uv).r > 0.5 ? 1.0 : 0.0;
                        sum += Lod0(_SrcTex, uv).rgb * v;
                        n += v;
                    }
                }
                return n > 0.0 ? float4(sum / n, c.a) : c;
            }
            ENDCG
        }

        // 6: PadValid（有効な範囲を 1 画素広げる）
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float4 frag(v2f_img i) : SV_Target
            {
                float v = 0.0;
                for (int y = -1; y <= 1; y++)
                    for (int x = -1; x <= 1; x++)
                        v = max(v, Lod0(_ValidTex, i.uv + float2(x, y) * _OutSize.zw).r);
                return v > 0.5 ? 1.0 : 0.0;
            }
            ENDCG
        }
    }
}
