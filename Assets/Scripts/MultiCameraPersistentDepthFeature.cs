using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

namespace GrayWolf.GPUInstancing.Domain
{
    public class MultiCameraPersistentDepthFeature : ScriptableRendererFeature
    {
        /// <summary>
        /// Returns the persistent depth texture for a specific camera
        /// </summary>
        public static RTHandle GetPersistentDepthTexture(int cameraInstanceID)
        {
            _persistentDepthTextures.TryGetValue(cameraInstanceID, out RTHandle handle);
            return handle;
        }

        /// <summary>
        /// Returns all camera instance IDs that have persistent depth textures
        /// </summary>
        public static List<int> GetActiveCameraIDs()
        {
            return new List<int>(_persistentDepthTextures.Keys);
        }

        private DepthCopyPass _depthPass;
        private static Dictionary<int, RTHandle> _persistentDepthTextures = new Dictionary<int, RTHandle>();
        private static Dictionary<int, int> _cameraShaderPropertyIDs = new Dictionary<int, int>();

        [SerializeField] private RenderPassEvent renderPassEvent;

        public override void Create()
        {
            _depthPass = new DepthCopyPass(renderPassEvent);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if(renderingData.cameraData.cameraType != CameraType.Game)
                return;

            int cameraID = renderingData.cameraData.camera.GetInstanceID();
        
            // Ensure our persistent depth RT is allocated with current camera size
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;                           // no depth buffer (we're storing depth as color)
            desc.msaaSamples = 1;                               // no MSAA for the depth texture
            desc.stencilFormat = GraphicsFormat.None;           // no stencil
            desc.graphicsFormat = GraphicsFormat.R32_SFloat;    // 32-bit float single channel

            // Get or create RTHandle for this camera
            if (!_persistentDepthTextures.TryGetValue(cameraID, out RTHandle persistentDepthTexture))
            {
                persistentDepthTexture = null;
            }

            RenderingUtils.ReAllocateHandleIfNeeded(ref persistentDepthTexture, desc, FilterMode.Point, TextureWrapMode.Clamp, name: $"_PersistentDepthTexture_Camera_{cameraID}");
            
            _persistentDepthTextures[cameraID] = persistentDepthTexture;
                
            if (persistentDepthTexture == null || persistentDepthTexture.rt == null)
            {
                Debug.LogError($"Persistent Depth RT is null for camera {cameraID}.");
                return;
            }

            // Create unique shader property ID for this camera if it doesn't exist
            if (!_cameraShaderPropertyIDs.TryGetValue(cameraID, out int shaderPropertyID))
            {
                string propertyName = $"_PersistentCameraDepth_{cameraID}";
                shaderPropertyID = Shader.PropertyToID(propertyName);
                _cameraShaderPropertyIDs[cameraID] = shaderPropertyID;
            }
        
            // Assign the RTHandle to our pass so it can import it
            _depthPass.Setup(persistentDepthTexture, shaderPropertyID);
        
            renderer.EnqueuePass(_depthPass);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            
            // Clean up all persistent depth textures
            foreach (var kvp in _persistentDepthTextures)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.Release();
                }
            }
            _persistentDepthTextures.Clear();
            _cameraShaderPropertyIDs.Clear();
        }

        /// <summary>
        /// Call this to clean up depth texture for a specific camera when it's destroyed
        /// </summary>
        public static void CleanupCameraDepthTexture(int cameraInstanceID)
        {
            if (_persistentDepthTextures.TryGetValue(cameraInstanceID, out RTHandle handle))
            {
                if (handle != null)
                {
                    handle.Release();
                }
                _persistentDepthTextures.Remove(cameraInstanceID);
                _cameraShaderPropertyIDs.Remove(cameraInstanceID);
            }
        }
    
        class DepthCopyPass : ScriptableRenderPass
        {
            private RTHandle _persistentDepthHandle;
            private int _shaderPropertyID;

            public DepthCopyPass(RenderPassEvent renderPassEvent)
            {
                this.renderPassEvent = renderPassEvent;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(RTHandle persistentDepthHandle, int shaderPropertyID)
            {
                this._persistentDepthHandle = persistentDepthHandle;
                this._shaderPropertyID = shaderPropertyID;
            }

            // RecordRenderGraph is called each frame to build the render graph pass
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
            {
                if (_persistentDepthHandle == null)
                    return;

                // Import the persistent RTHandle into the render graph (for writing)
                TextureHandle depthTarget = renderGraph.ImportTexture(_persistentDepthHandle);
                // Get the current camera depth (frame data) texture handle
                UniversalResourceData frameData = frameContext.Get<UniversalResourceData>();
                TextureHandle cameraDepth = frameData.cameraDepthTexture;
            
                if (!cameraDepth.IsValid())
                {
                    Debug.LogError("Camera depth texture is not valid!");
                    return;
                }
            
                renderGraph.AddBlitPass(cameraDepth, depthTarget, Vector2.one, Vector2.zero, passName: "Copy Depth To Persistent Texture");
                
                // Set the global shader property for this specific camera
                Shader.SetGlobalTexture(_shaderPropertyID, _persistentDepthHandle.rt);
            }
        }
    }
}