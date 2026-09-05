// Palette4_Fullscreen.shader
// URP full-screen post effect: crushes the image down to exactly 4 user-chosen colors.
// Works with URP's built-in "Full Screen Pass Renderer Feature" (Unity 2022.2+ / URP 14+).
Shader "Hidden/Custom/Palette4_Fullscreen"
{
    Properties
    {
        [Header(Palette)]
        _Color0 ("Color 0 (darkest)",  Color) = (0.058, 0.219, 0.058, 1)
        _Color1 ("Color 1",            Color) = (0.188, 0.384, 0.188, 1)
        _Color2 ("Color 2",            Color) = (0.545, 0.674, 0.058, 1)
        _Color3 ("Color 3 (lightest)", Color) = (0.608, 0.737, 0.058, 1)

        [Header(Matching)]
        [KeywordEnum(Luminance, Nearest)] _Match ("Match Mode", Float) = 0
        [Toggle(_GAMMA_MATCH)] _GammaMatch ("Match In Gamma Space", Float) = 1
        _Contrast   ("Contrast",   Range(0.1, 4)) = 1
        _Brightness ("Brightness", Range(-1, 1))  = 0

        [Header(Dither)]
        _DitherStrength ("Dither Strength",  Range(0, 1)) = 0.5
        _DitherScale    ("Dither Pixel Size", Range(1, 8)) = 1

        [Header(Blend)]
        _Blend ("Effect Amount", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "Palette4"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _MATCH_LUMINANCE _MATCH_NEAREST
            #pragma shader_feature_local_fragment _GAMMA_MATCH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            // URP 14+/Unity 6: Blit.hlsl ships in the core package, not universal.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color0;
                float4 _Color1;
                float4 _Color2;
                float4 _Color3;
                float  _Contrast;
                float  _Brightness;
                float  _DitherStrength;
                float  _DitherScale;
                float  _Blend;
            CBUFFER_END

            // 4x4 ordered Bayer matrix, normalized to [0,1)
            float Bayer4x4(int2 p)
            {
                const float m[16] =
                {
                     0.0 / 16,  8.0 / 16,  2.0 / 16, 10.0 / 16,
                    12.0 / 16,  4.0 / 16, 14.0 / 16,  6.0 / 16,
                     3.0 / 16, 11.0 / 16,  1.0 / 16,  9.0 / 16,
                    15.0 / 16,  7.0 / 16, 13.0 / 16,  5.0 / 16
                };
                int i = (p.y & 3) * 4 + (p.x & 3);
                return m[i];
            }

            // Matching happens here. Palette colors and scene color must be in the same space.
            float3 ToMatchSpace(float3 c)
            {
            #if defined(_GAMMA_MATCH) && !defined(UNITY_COLORSPACE_GAMMA)
                return LinearToSRGB(saturate(c));
            #else
                return c;
            #endif
            }

            void GetPalette(out float3 pal[4])
            {
                pal[0] = _Color0.rgb;
                pal[1] = _Color1.rgb;
                pal[2] = _Color2.rgb;
                pal[3] = _Color3.rgb;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv  = input.texcoord;
                float3 src = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;

                // Ordered dither value, optionally chunked into bigger pixels
                float2 pixelPos = uv * _ScreenParams.xy / max(_DitherScale, 1.0);
                float  bayer    = Bayer4x4((int2)floor(pixelPos));

                float3 pal[4];
                GetPalette(pal);

                // Bring everything into the same working space and apply tone tweaks
                float3 c = ToMatchSpace(src);
                c = saturate((c - 0.5) * _Contrast + 0.5 + _Brightness);

                float3 outColor;

            #if defined(_MATCH_NEAREST)
                // --- Nearest color in RGB, with dither between the two closest entries ---
                int   best = 0, second = 0;
                float bestD = 1e9, secondD = 1e9;

                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    float3 d = c - ToMatchSpace(pal[i]);
                    float  dist = dot(d, d);
                    if (dist < bestD)      { secondD = bestD; second = best; bestD = dist; best = i; }
                    else if (dist < secondD) { secondD = dist; second = i; }
                }

                float b = sqrt(bestD);
                float s = sqrt(secondD);
                float mixAmount = (b + s) > 1e-5 ? b / (b + s) : 0.0; // 0 = clearly best, 0.5 = tie
                int   idx = (bayer * _DitherStrength < mixAmount) ? second : best;
                outColor = pal[idx];
            #else
                // --- Luminance ramp: darkest -> lightest, palette order matters ---
                float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
                lum += (bayer - 0.5) * (1.0 / 3.0) * _DitherStrength;
                int idx = (int)clamp(round(saturate(lum) * 3.0), 0.0, 3.0);
                outColor = pal[idx];
            #endif

                return half4(lerp(src, outColor, _Blend), 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
