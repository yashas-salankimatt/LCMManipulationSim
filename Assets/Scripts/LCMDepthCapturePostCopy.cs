using UnityEngine;
using UnityEngine.Rendering;
using LCM.LCM;
using System;
using System.Collections.Generic;

public class LCMDepthCapturePostCopy : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private bool debugMode = true;
    
    [Header("LCM Settings")]
    [SerializeField] private string lcmURL = "udpm://239.255.76.67:7667";
    [SerializeField] private string encoding = "32FC1"; // 32-bit float, 1 channel
    
    [System.Serializable]
    public class CameraConfig
    {
        public Camera camera;
        public string topicName = "head_cam_depth#sensor_msgs.Image";
        public string frameId = "head_camera_depth_optical_frame";
        [HideInInspector] public Texture2D cachedDepthTexture;
        [HideInInspector] public bool needsCapture = false;
    }
    
    [SerializeField] private List<CameraConfig> cameraConfigs = new List<CameraConfig>();
    
    [Header("Output Options")]
    [SerializeField] private KeyCode captureKey = KeyCode.C;
    [SerializeField] private bool continuousMode = false;
    [SerializeField] private float publishRate = 10f; // Hz (for continuous mode)
    
    private LCM.LCM.LCM lcm;
    private float nextCaptureTime = 0f;
    private Dictionary<Camera, CameraConfig> cameraToConfig = new Dictionary<Camera, CameraConfig>();
    
    void Start()
    {
        // Map cameras to their configs for quick lookup
        foreach (var config in cameraConfigs)
        {
            if (config.camera == null)
            {
                Debug.LogError("Camera not assigned in config!");
                continue;
            }
            
            // Enable depth texture mode on the camera
            config.camera.depthTextureMode = DepthTextureMode.Depth;
            
            // Add to lookup dictionary
            cameraToConfig[config.camera] = config;
        }
        
        // Subscribe to render pipeline events
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        
        try
        {
            // Initialize LCM
            lcm = new LCM.LCM.LCM(lcmURL);
            
            if (debugMode)
            {
                Debug.Log($"Depth capture ready. Cameras configured: {cameraConfigs.Count}");
                Debug.Log($"LCM initialized with URL: {lcmURL}");
                if (continuousMode)
                    Debug.Log($"Continuous mode enabled: Publishing at {publishRate} Hz");
                else
                    Debug.Log($"Press '{captureKey}' key to capture and publish depth");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error during LCM initialization: " + ex.Message);
            Debug.LogException(ex);
        }
    }
    
    void Update()
    {
        if (lcm == null)
            return;
            
        if (continuousMode)
        {
            // Check if it's time for the next capture in continuous mode
            if (Time.time >= nextCaptureTime)
            {
                // Mark all cameras for capture
                foreach (var config in cameraConfigs)
                {
                    config.needsCapture = true;
                }
                nextCaptureTime = Time.time + (1f / publishRate);
            }
        }
        else
        {
            // Capture depth when the capture key is pressed
            if (Input.GetKeyDown(captureKey))
            {
                // Mark all cameras for capture
                foreach (var config in cameraConfigs)
                {
                    config.needsCapture = true;
                }
                if (debugMode) Debug.Log("Depth capture and publish requested...");
            }
        }
    }
    
    void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        // Nothing needed here for this approach
    }
    
    void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        // Check if this is one of our tracked cameras
        if (!cameraToConfig.ContainsKey(camera))
            return;
        
        var config = cameraToConfig[camera];
        
        // Check if we need to capture for this camera
        if (!config.needsCapture)
            return;
        
        // Get the global depth texture immediately after this camera renders
        Texture depthTexture = Shader.GetGlobalTexture("_CameraDepthTexture");
        
        if (depthTexture == null || depthTexture.width <= 4 || depthTexture.height <= 4)
        {
            if (debugMode)
            {
                Debug.LogWarning($"Depth texture not available for camera {camera.name}! Size: {depthTexture?.width}x{depthTexture?.height}");
                Debug.LogWarning("Make sure 'Depth Texture' is enabled in your URP Asset and camera is rendering to display or render texture!");
            }
            config.needsCapture = false;
            return;
        }
        
        // Copy and process the depth texture
        CaptureAndPublishDepth(depthTexture, config);
        
        // Reset capture flag
        config.needsCapture = false;
    }
    
    private void CaptureAndPublishDepth(Texture sourceTexture, CameraConfig config)
    {
        int width = sourceTexture.width;
        int height = sourceTexture.height;
        
        if (debugMode)
            Debug.Log($"Capturing depth texture for {config.camera.name}: {width}x{height}");
        
        // Create a temporary render texture to copy the depth
        RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RFloat);
        
        // Copy the depth texture using a simple blit
        Graphics.Blit(sourceTexture, tempRT);
        
        // Read the render texture into a Texture2D
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = tempRT;
        
        // Create or reuse the cached texture
        if (config.cachedDepthTexture == null || 
            config.cachedDepthTexture.width != width || 
            config.cachedDepthTexture.height != height)
        {
            if (config.cachedDepthTexture != null)
                DestroyImmediate(config.cachedDepthTexture);
            
            config.cachedDepthTexture = new Texture2D(width, height, TextureFormat.RFloat, false);
        }
        
        config.cachedDepthTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        config.cachedDepthTexture.Apply();
        
        RenderTexture.active = previousActive;
        
        // Get raw depth values and linearize them
        Color[] rawDepthPixels = config.cachedDepthTexture.GetPixels();
        float[] linearDepthValues = LinearizeDepthValues(rawDepthPixels, config.camera);
        
        // Publish the depth data to LCM
        PublishDepthToLCM(linearDepthValues, width, height, config);
        
        // Cleanup
        RenderTexture.ReleaseTemporary(tempRT);
    }
    
    private float[] LinearizeDepthValues(Color[] rawDepthPixels, Camera camera)
    {
        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        
        float[] linearDepths = new float[rawDepthPixels.Length];
        
        // Debug tracking
        float minLinear = float.MaxValue;
        float maxLinear = float.MinValue;
        float minRaw = float.MaxValue;
        float maxRaw = float.MinValue;
        int validPixels = 0;
        
        for (int i = 0; i < rawDepthPixels.Length; i++)
        {
            float rawDepth = rawDepthPixels[i].r;
            // Using the same linearization method from original script
            float linearDepth = CorrectPerspectiveLinearization(rawDepth, near, far);
            
            linearDepths[i] = linearDepth;
            
            // Track stats for debugging
            if (!float.IsInfinity(linearDepth) && linearDepth > 0 && linearDepth < far * 2)
            {
                minLinear = Mathf.Min(minLinear, linearDepth);
                maxLinear = Mathf.Max(maxLinear, linearDepth);
                validPixels++;
            }
            
            // Track raw depth range too
            minRaw = Mathf.Min(minRaw, rawDepth);
            maxRaw = Mathf.Max(maxRaw, rawDepth);
        }
        
        if (debugMode)
        {
            Debug.Log($"=== Depth Analysis for {camera.name} ===");
            Debug.Log($"Raw depth range: {minRaw:F6} to {maxRaw:F6}");
            Debug.Log($"Linearized depth range: {minLinear:F3}m to {maxLinear:F3}m");
            Debug.Log($"Valid pixels: {validPixels}/{rawDepthPixels.Length}");
            Debug.Log($"Camera near: {near:F3}m, far: {far:F3}m");
            Debug.Log($"Platform info: Reversed Z = {SystemInfo.usesReversedZBuffer}");
        }
        
        return linearDepths;
    }
    
    private float CorrectPerspectiveLinearization(float rawDepth, float near, float far)
    {
        // This is the correct way to linearize perspective projection depth
        float depth01;
        
        if (SystemInfo.usesReversedZBuffer)
        {
            // Reversed Z: 1.0 = near, 0.0 = far
            // Convert to standard 0=near, 1=far format
            depth01 = 1.0f - rawDepth;
        }
        else
        {
            // Standard Z: 0.0 = near, 1.0 = far
            depth01 = rawDepth;
        }
        
        // Handle edge cases
        if (depth01 <= 0.0001f) return near;  // Very close to near plane
        if (depth01 >= 0.9999f) return far;   // Very close to far plane
        
        // Correct perspective projection linearization formula
        // This accounts for the non-linear distribution of depth values
        float linearDepth = (near * far) / (far - depth01 * (far - near));
        
        // Clamp to reasonable bounds
        return Mathf.Clamp(linearDepth, near, far * 2.0f);
    }
    
    private void PublishDepthToLCM(float[] depthValues, int width, int height, CameraConfig config)
    {
        try
        {
            // Create the Image message
            sensor_msgs.Image imageMsg = new sensor_msgs.Image();
            
            // Set header
            imageMsg.header = new std_msgs.Header();
            imageMsg.header.seq = 0; // You could implement a sequence counter here
            imageMsg.header.stamp = new std_msgs.Time();
            imageMsg.header.stamp.sec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            imageMsg.header.stamp.nsec = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000) * 1000000);
            imageMsg.header.frame_id = config.frameId;
            
            // Set image properties
            imageMsg.height = height;
            imageMsg.width = width;
            imageMsg.encoding = encoding; // 32-bit float, 1 channel
            imageMsg.is_bigendian = BitConverter.IsLittleEndian ? (byte)0 : (byte)1;
            imageMsg.step = width * sizeof(float); // Bytes per row
            
            // Convert float array to byte array
            int dataSize = depthValues.Length * sizeof(float);
            imageMsg.data = new byte[dataSize];
            imageMsg.data_length = dataSize;
            
            // Copy float values to byte array (handling endianness)
            Buffer.BlockCopy(depthValues, 0, imageMsg.data, 0, dataSize);
            
            // Publish to LCM
            lcm.Publish(config.topicName, imageMsg);
            
            if (debugMode)
                Debug.Log($"Published depth image ({width}x{height}) from {config.camera.name} to topic: {config.topicName}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error publishing depth to LCM: {ex.Message}");
            Debug.LogException(ex);
        }
    }
    
    void OnDestroy()
    {
        // Unsubscribe from render pipeline events
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        
        // Clean up cached textures
        foreach (var config in cameraConfigs)
        {
            if (config.cachedDepthTexture != null)
            {
                DestroyImmediate(config.cachedDepthTexture);
            }
        }
    }
    
    void OnDisable()
    {
        // Optional: Reset depth texture mode if needed
        foreach (var config in cameraConfigs)
        {
            if (config.camera != null)
            {
                // You might want to keep depth texture enabled for other effects
                // config.camera.depthTextureMode = DepthTextureMode.None;
            }
        }
    }
}