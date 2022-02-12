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