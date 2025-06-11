using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

public class DepthCopyFeature : ScriptableRendererFeature
{
    class DepthCopyPass : ScriptableRenderPass
    {
        // Shader property for userscripts to fetch:
        readonly int _GlobalDepthID;
        // Temporary RT identifier to hold our copy:
        readonly int _TempDepthID;

        RenderTargetIdentifier _cameraDepthRT;  // source
        RenderTargetIdentifier _copyDepthRT;    // destination

        RenderTextureDescriptor _copyDesc;

        public DepthCopyPass(string cameraName)
        {
            // Build a unique name per-camera:
            var propName = $"_DepthCopy_{cameraName}";
            Debug.Log($"DepthCopyPass created for camera: {cameraName}, using property name: {propName}");
            _GlobalDepthID = Shader.PropertyToID(propName);
            _TempDepthID   = Shader.PropertyToID(propName + "_Temp");
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        // Called once per camera, before Execute
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // Source is URP's built-in depth texture
            _cameraDepthRT = new RenderTargetIdentifier("_CameraDepthTexture");

            // Build our copy RT descriptor
            var baseDesc = renderingData.cameraData.cameraTargetDescriptor;
            _copyDesc = baseDesc;
            _copyDesc.graphicsFormat = GraphicsFormat.R32_SFloat; // 32-bit float
            _copyDesc.depthBufferBits = 0;                      // no depth
            _copyDesc.msaaSamples = 1;                          // no MSAA

            // Allocate the temporary RT
            cmd.GetTemporaryRT(_TempDepthID, _copyDesc, FilterMode.Point);
            _copyDepthRT = new RenderTargetIdentifier(_TempDepthID);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get("DepthCopyPass");

            // Blit the camera's depth into our RFloat RT
            cmd.Blit(_cameraDepthRT, _copyDepthRT);

            // Expose it globally so any shader / Script can fetch it
            cmd.SetGlobalTexture(_GlobalDepthID, _copyDepthRT);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // Release our temp RT
            cmd.ReleaseTemporaryRT(_TempDepthID);
        }
    }

    DepthCopyPass _pass;

    public override void Create()
    {
        // We defer constructing the pass until AddRenderPasses
        _pass = null;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // Create a new pass per-camera (so the name is unique)
        var camName = renderingData.cameraData.camera.name;
        _pass = new DepthCopyPass(camName);

        // Enqueue it
        renderer.EnqueuePass(_pass);
    }
}
