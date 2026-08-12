Shader "Kitchen/ToonCel"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)

        [Header(Cel Shading)]
        _ShadowColor ("Shadow Color", Color) = (0.55, 0.6, 0.75, 1)
        _MidToneColor ("Mid Tone Color", Color) = (0.85, 0.85, 0.9, 1)
        _ShadowThreshold ("Shadow Threshold", Range(0, 1)) = 0.45
        _MidThreshold ("Mid Threshold", Range(0, 1)) = 0.7
        _ShadowSmooth ("Shade Softness", Range(0.001, 0.2)) = 0.02
        _AmbientStrength ("Ambient Strength", Range(0, 1)) = 0.35

        [Header(Specular)]
        [Toggle(_SPECULAR_ON)] _SpecularOn ("Enable Specular", Float) = 0
        _SpecularColor ("Specular Color", Color) = (1, 1, 1, 1)
        _SpecularSize ("Specular Size", Range(0.01, 1)) = 0.2
        _SpecularSmooth ("Specular Softness", Range(0.001, 0.2)) = 0.02

        [Header(Rim)]
        [Toggle(_RIM_ON)] _RimOn ("Enable Rim", Float) = 1
        _RimColor ("Rim Color", Color) = (1, 1, 1, 1)
        _RimPower ("Rim Power", Range(0.5, 8)) = 3
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.35

        [Header(Outline)]
        [Toggle(_OUTLINE_ON)] _OutlineOn ("Enable Outline", Float) = 1
        _OutlineColor ("Outline Color", Color) = (0.05, 0.05, 0.08, 1)
        _OutlineWidth ("Outline Width", Range(0, 0.05)) = 0.008
        _OutlineZOffset ("Outline Z Offset", Range(0, 0.05)) = 0.0

        [Header(Alpha Clip)]
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clip", Float) = 0
        _Cutoff ("Cutoff", Range(0, 1)) = 0.5

        // Keep Unity material conversion from URP Lit from forcing transparent blends.
        [HideInInspector] _Surface ("__surface", Float) = 0.0
        [HideInInspector] _Blend ("__blend", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha ("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha ("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
        [HideInInspector] _Cull ("__cull", Float) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }

        // ---------------- Forward Lit (Cel) — draw first ----------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ToonVert
            #pragma fragment ToonFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SPECULAR_ON
            #pragma shader_feature_local_fragment _RIM_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _ShadowColor;
                half4 _MidToneColor;
                half _ShadowThreshold;
                half _MidThreshold;
                half _ShadowSmooth;
                half _AmbientStrength;
                half4 _SpecularColor;
                half _SpecularSize;
                half _SpecularSmooth;
                half4 _RimColor;
                half _RimPower;
                half _RimStrength;
                half4 _OutlineColor;
                half _OutlineWidth;
                half _OutlineZOffset;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            half SmoothBand(half value, half edge, half softness)
            {
                return smoothstep(edge - softness, edge + softness, value);
            }

            half3 CelShade(half litAmount, half3 albedo)
            {
                half mid = SmoothBand(litAmount, _ShadowThreshold, _ShadowSmooth);
                half hi = SmoothBand(litAmount, _MidThreshold, _ShadowSmooth);
                half3 shadowCol = albedo * _ShadowColor.rgb;
                half3 midCol = albedo * _MidToneColor.rgb;
                half3 shade = lerp(shadowCol, midCol, mid);
                return lerp(shade, albedo, hi);
            }

            Varyings ToonVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return output;
            }

            half4 ToonFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 albedoSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = albedoSample.rgb * _BaseColor.rgb;
                half alpha = albedoSample.a * _BaseColor.a;

                #if defined(_ALPHATEST_ON)
                    clip(alpha - _Cutoff);
                #endif

                float3 normalWS = normalize(input.normalWS);
                float3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half litAmount = ndotl * mainLight.shadowAttenuation;
                half3 color = CelShade(litAmount, albedo) * mainLight.color;

                // Flat ambient so unlit areas stay readable (also avoids "invisible" dark indoor scenes).
                color += albedo * _AmbientStrength;

                #if defined(_SPECULAR_ON)
                    float3 halfDir = SafeNormalize(mainLight.direction + viewDirWS);
                    half ndoth = saturate(dot(normalWS, halfDir));
                    half specMask = 1.0h - SmoothBand(1.0h - ndoth, 1.0h - _SpecularSize, _SpecularSmooth);
                    color += _SpecularColor.rgb * specMask * mainLight.color * mainLight.shadowAttenuation;
                #endif

                #if defined(_RIM_ON)
                    half rim = 1.0h - saturate(dot(normalWS, viewDirWS));
                    rim = pow(max(rim, 1e-4h), _RimPower) * _RimStrength;
                    color += _RimColor.rgb * rim;
                #endif

                #if defined(_ADDITIONAL_LIGHTS)
                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light light = GetAdditionalLight(lightIndex, input.positionWS);
                        half addNdotL = saturate(dot(normalWS, light.direction));
                        half addAtten = light.distanceAttenuation * light.shadowAttenuation;
                        half band = SmoothBand(addNdotL, _ShadowThreshold, _ShadowSmooth);
                        color += albedo * light.color * lerp(_ShadowColor.rgb * 0.25h, half3(1, 1, 1), band) * addAtten;
                    LIGHT_LOOP_END
                #endif

                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------------- Outline (Inverted Hull) — after lit, no depth write ----------------
        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend One Zero
            ColorMask RGB

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex OutlineVert
            #pragma fragment OutlineFrag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _OUTLINE_ON
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _ShadowColor;
                half4 _MidToneColor;
                half _ShadowThreshold;
                half _MidThreshold;
                half _ShadowSmooth;
                half _AmbientStrength;
                half4 _SpecularColor;
                half _SpecularSize;
                half _SpecularSmooth;
                half4 _RimColor;
                half _RimPower;
                half _RimStrength;
                half4 _OutlineColor;
                half _OutlineWidth;
                half _OutlineZOffset;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings OutlineVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                #ifndef _OUTLINE_ON
                    // Degenerate — skip drawing when outline disabled.
                    output.positionCS = float4(0, 0, 0, 1);
                    return output;
                #else
                    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                    float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                    posWS += SafeNormalize(normalWS) * _OutlineWidth;
                    output.positionCS = TransformWorldToHClip(posWS);

                    #if UNITY_REVERSED_Z
                        output.positionCS.z -= _OutlineZOffset * output.positionCS.w;
                    #else
                        output.positionCS.z += _OutlineZOffset * output.positionCS.w;
                    #endif

                    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                    return output;
                #endif
            }

            half4 OutlineFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                #ifndef _OUTLINE_ON
                    clip(-1);
                #endif

                #if defined(_ALPHATEST_ON)
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(a - _Cutoff);
                #endif

                return half4(_OutlineColor.rgb, 1);
            }
            ENDHLSL
        }

        // ---------------- Shadow Caster ----------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _ShadowColor;
                half4 _MidToneColor;
                half _ShadowThreshold;
                half _MidThreshold;
                half _ShadowSmooth;
                half _AmbientStrength;
                half4 _SpecularColor;
                half _SpecularSize;
                half _SpecularSmooth;
                half4 _RimColor;
                half _RimPower;
                half _RimStrength;
                half4 _OutlineColor;
                half _OutlineWidth;
                half _OutlineZOffset;
                half _Cutoff;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 GetShadowPositionHClip(Attributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.positionCS = GetShadowPositionHClip(input);
                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                #if defined(_ALPHATEST_ON)
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(a - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ---------------- Depth Only ----------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _ShadowColor;
                half4 _MidToneColor;
                half _ShadowThreshold;
                half _MidThreshold;
                half _ShadowSmooth;
                half _AmbientStrength;
                half4 _SpecularColor;
                half _SpecularSize;
                half _SpecularSmooth;
                half4 _RimColor;
                half _RimPower;
                half _RimStrength;
                half4 _OutlineColor;
                half _OutlineWidth;
                half _OutlineZOffset;
                half _Cutoff;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthOnlyVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthOnlyFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                #if defined(_ALPHATEST_ON)
                    half a = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                    clip(a - _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
