using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class BokehDepthOfFieldFeature : ScriptableRendererFeature
{
    readonly Lazy<BokehDepthOfFieldPass> m_ScriptablePass = new Lazy<BokehDepthOfFieldPass>(() => new BokehDepthOfFieldPass()
    {
        renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing,
    });

    public class BokehDepthOfFieldPass : ScriptableRenderPass
    {
        static readonly int _SourceTex = Shader.PropertyToID("_SourceTex");
        static readonly int _FullCoCTexture = Shader.PropertyToID("_FullCoCTexture");
        static readonly int _DofTexture = Shader.PropertyToID("_DofTexture");
        static readonly int _CoCParams = Shader.PropertyToID("_CoCParams");
        static readonly int _BokehKernel = Shader.PropertyToID("_BokehKernel2");
        static readonly int _PongTexture = Shader.PropertyToID("_PongTexture");
        static readonly int _PingTexture = Shader.PropertyToID("_PingTexture");
        static readonly int _DownSampleScaleFactor = Shader.PropertyToID("_DownSampleScaleFactor");
        static readonly int _SourceSize = Shader.PropertyToID("_SourceSize");
        readonly Material m_Material = new Material(Shader.Find("BokehDepthOfField"));

        // Misc
        readonly Vector4[] m_BokehKernel = new Vector4[512];
        readonly ProfilingSampler m_Sampler = new ProfilingSampler(nameof(BokehDepthOfField));
        int m_BokehHash;

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var setting = VolumeManager.instance.stack.GetComponent<BokehDepthOfField>(); 
            if (!setting.IsActive() || renderingData.cameraData.isSceneViewCamera) { return; }

            var descriptor = renderingData.cameraData.cameraTargetDescriptor;

            var cmd = CommandBufferPool.Get();
            using(new ProfilingScope(cmd, m_Sampler))
            {
                int downSample = 2;
                var material = m_Material;
                int wh = descriptor.width / downSample;
                int hh = descriptor.height / downSample;

                // "A Lens and Aperture Camera Model for Synthetic Image Generation" [Potmesil81]
                float F = setting.focalLength.value / 1000f;
                float A = setting.focalLength.value / setting.aperture.value;
                float P = setting.focusDistance.value;
                float maxCoC = (A * F) / (P - F);
                float maxRadius = GetMaxBokehRadiusInPixels(descriptor.height);
                float rcpAspect = 1f / (wh / (float)hh);

                cmd.SetGlobalVector(_CoCParams, new Vector4(P, maxCoC, maxRadius, rcpAspect));

                // Prepare the bokeh kernel constant buffer
                int hash = setting.GetHashCode();
                if (hash != m_BokehHash)
                {
                    m_BokehHash = hash;
                    PrepareBokehKernel(setting, m_BokehKernel);
                }

                cmd.SetGlobalVectorArray(_BokehKernel, m_BokehKernel);

                // Temporary textures
                cmd.GetTemporaryRT(_SourceTex, descriptor, FilterMode.Bilinear);
                cmd.GetTemporaryRT(_FullCoCTexture, new RenderTextureDescriptor(descriptor.width, descriptor.height, GraphicsFormat.R8_UNorm, 0), FilterMode.Bilinear);
                cmd.GetTemporaryRT(_PingTexture, new RenderTextureDescriptor(wh, hh, GraphicsFormat.R16G16B16A16_SFloat, 0), FilterMode.Bilinear);
                cmd.GetTemporaryRT(_PongTexture, new RenderTextureDescriptor(wh, hh, GraphicsFormat.R16G16B16A16_SFloat, 0), FilterMode.Bilinear);

                cmd.CopyTexture(renderingData.cameraData.renderer.cameraColorTarget, _SourceTex);

                SetSourceSize(cmd, descriptor);
                cmd.SetGlobalVector(_DownSampleScaleFactor, new Vector4(1.0f / downSample, 1.0f / downSample, downSample, downSample));

                // Compute CoC
                cmd.SetGlobalTexture(_SourceTex, _SourceTex);
                cmd.Blit(null, _FullCoCTexture, material, 0);
                cmd.SetGlobalTexture(_FullCoCTexture, _FullCoCTexture);

                // Downscale & prefilter color + coc
                cmd.SetGlobalTexture(_SourceTex, _SourceTex);
                cmd.Blit(null, _PingTexture, material, 1);

                // Bokeh blur
                cmd.SetGlobalTexture(_SourceTex, _PingTexture);
                cmd.Blit(null, _PongTexture, material, 2);

                // Post-filtering
                cmd.SetGlobalTexture(_SourceTex, _PongTexture);
                cmd.Blit(null, _PingTexture, material, 3);

                // Composite
                cmd.SetGlobalTexture(_DofTexture, _PingTexture);
                cmd.SetGlobalTexture(_SourceTex, _SourceTex);
                cmd.Blit(null, renderingData.cameraData.renderer.cameraColorTarget, material, 4);

                // Cleanup
                cmd.ReleaseTemporaryRT(_SourceTex);
                cmd.ReleaseTemporaryRT(_FullCoCTexture);
                cmd.ReleaseTemporaryRT(_PingTexture);
                cmd.ReleaseTemporaryRT(_PongTexture);

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        #region Depth Of Field

        public static void PrepareBokehKernel(BokehDepthOfField depthOfField, Vector4[] bokehKernel)
        {
            const int kRings = 4;
            const int kPointsPerRing = 7;

            // Fill in sample points (concentric circles transformed to rotated N-Gon)
            int idx = 0;
            float bladeCount = depthOfField.bladeCount.value;
            float curvature = 1f - depthOfField.bladeCurvature.value;
            float rotation = depthOfField.bladeRotation.value * Mathf.Deg2Rad;
            const float PI = Mathf.PI;
            const float TWO_PI = Mathf.PI * 2f;

            for (int ring = 1; ring < kRings; ring++)
            {
                float bias = 1f / kPointsPerRing;
                float radius = (ring + bias) / (kRings - 1f + bias);
                int points = ring * kPointsPerRing;

                for (int point = 0; point < points; point++)
                {
                    // Angle on ring
                    float phi = 2f * PI * point / points;

                    // Transform to rotated N-Gon
                    // Adapted from "CryEngine 3 Graphics Gems" [Sousa13]
                    float nt = Mathf.Cos(PI / bladeCount);
                    float dt = Mathf.Cos(phi - (TWO_PI / bladeCount) * Mathf.Floor((bladeCount * phi + Mathf.PI) / TWO_PI));
                    float r = radius * Mathf.Pow(nt / dt, curvature);
                    float u = r * Mathf.Cos(phi - rotation);
                    float v = r * Mathf.Sin(phi - rotation);

                    bokehKernel[idx] = new Vector4(u, v);
                    idx++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float GetMaxBokehRadiusInPixels(float viewportHeight)
        {
            // Estimate the maximum radius of bokeh (empirically derived from the ring count)
            const float kRadiusInPixels = 14f;
            return Mathf.Min(0.05f, kRadiusInPixels / viewportHeight);
        }

        internal static void SetSourceSize(CommandBuffer cmd, RenderTextureDescriptor desc)
        {
            float width = desc.width;
            float height = desc.height;
            if (desc.useDynamicScale)
            {
                width *= ScalableBufferManager.widthScaleFactor;
                height *= ScalableBufferManager.heightScaleFactor;
            }
            cmd.SetGlobalVector(_SourceSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
        }

        #endregion

    }


    public override void Create() {}

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        => renderer.EnqueuePass(m_ScriptablePass.Value);
}


