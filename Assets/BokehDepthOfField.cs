using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[Serializable, VolumeComponentMenu("Bokeh Depth Of Field")]
public sealed class BokehDepthOfField : VolumeComponent, IPostProcessComponent
{
    public BoolParameter isActive = new BoolParameter(false);
    public MinFloatParameter focusDistance = new MinFloatParameter(10f, 0.1f);
    public ClampedFloatParameter aperture = new ClampedFloatParameter(5.6f, 1f, 32f);
    public ClampedFloatParameter focalLength = new ClampedFloatParameter(50f, 1f, 300f);
    public ClampedIntParameter bladeCount = new ClampedIntParameter(5, 3, 9);
    public ClampedFloatParameter bladeCurvature = new ClampedFloatParameter(1f, 0f, 1f);
    public ClampedFloatParameter bladeRotation = new ClampedFloatParameter(0f, -180f, 180f);
    public bool IsActive() => isActive.value;
    public bool IsTileCompatible() => false;
}

namespace _
{
    using System.Linq;
    using UnityEditor;
    using UnityEditor.Rendering;
    using UnityEngine.Experimental.Rendering;

    [VolumeComponentEditor(typeof(BokehDepthOfField))]
    sealed class BokehDepthOfFieldEditor : VolumeComponentEditor
    {
        readonly Lazy<RenderTexture> m_Preview = new Lazy<RenderTexture>(()
            => new RenderTexture(128, 128, 0, GraphicsFormat.R8G8B8A8_UNorm) { hideFlags = HideFlags.DontSaveInEditor, enableRandomWrite = true });
        // readonly Lazy<Texture2D> m_Preview = new Lazy<Texture2D>(()
        //     => new Texture2D(128, 128) { hideFlags = HideFlags.DontSaveInEditor });
        readonly Lazy<Material> m_Material = new Lazy<Material>(()
            => new Material(Shader.Find("BokehDepthOfField")) { hideFlags = HideFlags.DontSaveInEditor });
        readonly Lazy<ComputeShader> m_ComputeShader = new Lazy<ComputeShader>(()
            => AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/BokehDepthOfField.compute"));
        static readonly Vector4[] m_BokehKernel = new Vector4[512];

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            
            // PlotByPixelShader(m_Preview.Value, (BokehDepthOfField)target);
            PlotByComputeShader(m_Preview.Value, (BokehDepthOfField)target);
            // PlotByCPU(m_Preview.Value, (BokehDepthOfField)target);

            EditorGUILayout.LabelField("Kernel Preview");
            using(new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(new GUIContent(m_Preview.Value), GUILayout.Width(m_Preview.Value.width), GUILayout.Height(m_Preview.Value.height));
            }
        }

        void PlotByPixelShader(RenderTexture target, BokehDepthOfField setting)
        {
            var cmd = CommandBufferPool.Get();
            {
                cmd.Clear();
                cmd.SetRenderTarget(target);
                cmd.ClearRenderTarget(true, true, Color.black, 0);
                BokehDepthOfFieldFeature.BokehDepthOfFieldPass.PrepareBokehKernel(setting, m_BokehKernel);
                cmd.SetGlobalVectorArray("_BokehKernel2", m_BokehKernel);
                cmd.SetGlobalVector("_TargetSize", new Vector4(target.width, target.height));
                cmd.Blit(null, target, m_Material.Value, 5);
            }
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void PlotByComputeShader(RenderTexture target, BokehDepthOfField setting)
        {
            var cmd = CommandBufferPool.Get();
            {
                cmd.Clear();
                cmd.SetRenderTarget(target);
                cmd.ClearRenderTarget(true, true, Color.black, 0);
                BokehDepthOfFieldFeature.BokehDepthOfFieldPass.PrepareBokehKernel(setting, m_BokehKernel);
                m_ComputeShader.Value.SetVectorArray("_BokehKernel2", m_BokehKernel);
                m_ComputeShader.Value.SetVector("_TargetSize", new Vector4(target.width, target.height));
                m_ComputeShader.Value.SetTexture(0, "_Target", target);
                
                cmd.DispatchCompute(m_ComputeShader.Value, m_ComputeShader.Value.FindKernel("CSPlot"), 1, 1, 1);
            } 
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void PlotByCPU(Texture2D target, BokehDepthOfField setting)
        {
            target.SetPixels(0, 0, target.width, target.height, Enumerable.Repeat(Color.black, target.width * target.height).ToArray());
            BokehDepthOfFieldFeature.BokehDepthOfFieldPass.PrepareBokehKernel(setting, m_BokehKernel);
            for(uint i = 0; i < 42; i++)
            {
                var index = Vector2.Scale(m_BokehKernel[i] / 2 + Vector4.one * 0.5f, new Vector2(target.width, target.height));
                target.SetPixel(Mathf.RoundToInt(index.x), Mathf.RoundToInt(index.y), Color.white);
            }
            target.Apply();
        }
    }
}