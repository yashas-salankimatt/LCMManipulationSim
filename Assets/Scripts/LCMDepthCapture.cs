using UnityEngine;
using UnityEngine.Rendering;
using LCM.LCM;
using System;

public class LCMDepthCapture : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private bool debugMode = true;
    
    [Header("LCM Settings")]
    [SerializeField] private string lcmURL = "udpm://239.255.76.67:7667";
    [SerializeField] private string topicName = "head_cam_depth#sensor_msgs.Image";
    [SerializeField] private string encoding = "32FC1"; // 32-bit float, 1 channel
    
    [Header("Output Options")]
    [SerializeField] private KeyCode captureKey = KeyCode.C;
    [SerializeField] private bool continuousMode = false;
    [SerializeField] private float publishRate = 10f; // Hz (for continuous mode)
    
    public Camera targetCamera;
    private LCM.LCM.LCM lcm;
    private bool captureRequested = false;
    private float nextCaptureTime = 0f;
    
    void Start()
    {
        // Get the main camera if not assigned
        if (targetCamera == null)
            targetCamera = Camera.main ?? FindObjectOfType<Camera>();
        
        if (targetCamera == null)
        {
            Debug.LogError("No camera found!");
            return;
        }
        
        // CRITICAL: Enable depth texture on the camera for URP
        targetCamera.depthTextureMode = DepthTextureMode.Depth;
        
        try
        {
            // Initialize LCM
            lcm = new LCM.LCM.LCM(lcmURL);
            
            if (debugMode)
            {
                Debug.Log($"Depth capture ready. Camera: {targetCamera.name}");
                Debug.Log($"Camera near: {targetCamera.nearClipPlane}m, far: {targetCamera.farClipPlane}m");
                Debug.Log($"LCM initialized with URL: {lcmURL}");
                Debug.Log($"Publishing to topic: {topicName}");
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
                captureRequested = true;
                nextCaptureTime = Time.time + (1f / publishRate);
            }
        }
        else
        {
            // Capture depth when the capture key is pressed
            if (Input.GetKeyDown(captureKey))
            {
                captureRequested = true;
                if (debugMode) Debug.Log("Depth capture and publish requested...");
            }
        }
    }
    
    void OnRenderObject()
    {
        Debug.Log("OnRenderObject called for camera: " + targetCamera.name);
        // This is the key: In Unity 6.0 URP, depth texture is only available here
        if (!captureRequested) return;
        
        captureRequested = false;

        // Get the global depth texture
        Texture depthTexture = Shader.GetGlobalTexture("_CameraDepthTexture");
        if (depthTexture == null)
        {
            // Try to get the depth texture from the camera directly
            Debug.LogError("Global depth texture not found! Attempting to get from camera.");
        }
        
        
        if (depthTexture == null || depthTexture.width <= 4 || depthTexture.height <= 4)
        {
            if (debugMode)
            {
                Debug.LogWarning($"Depth texture not available! Size: {depthTexture?.width}x{depthTexture?.height}");
                Debug.LogWarning("Make sure 'Depth Texture' is enabled in your URP Asset!");
            }
            return;
        }
        
        CaptureAndPublishDepth(depthTexture);
    }
    
    private void CaptureAndPublishDepth(Texture sourceTexture)
    {
        int width = sourceTexture.width;
        int height = sourceTexture.height;
        
        if (debugMode)
            Debug.Log($"Capturing depth texture: {width}x{height}");
        
        // Create a temporary render texture to copy the depth
        RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RFloat);
        
        // Copy the depth texture using a simple blit
        Graphics.Blit(sourceTexture, tempRT);
        
        // Read the render texture into a Texture2D
        RenderTexture.active = tempRT;
        Texture2D depthImage = new Texture2D(width, height, TextureFormat.RFloat, false);
        depthImage.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        depthImage.Apply();
        RenderTexture.active = null;
        
        // Get raw depth values and linearize them
        Color[] rawDepthPixels = depthImage.GetPixels();
        float[] linearDepthValues = LinearizeDepthValues(rawDepthPixels);
        
        // Publish the depth data to LCM
        PublishDepthToLCM(linearDepthValues, width, height);
        
        // Cleanup
        RenderTexture.ReleaseTemporary(tempRT);
        DestroyImmediate(depthImage);
    }
    
    private float[] LinearizeDepthValues(Color[] rawDepthPixels)
    {
        float near = targetCamera.nearClipPlane;
        float far = targetCamera.farClipPlane;
        
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
            // Using method 4 (CorrectPerspectiveLinearization) as specified
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
            Debug.Log($"=== Depth Analysis ===");
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
    
    private void PublishDepthToLCM(float[] depthValues, int width, int height)
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
            imageMsg.header.frame_id = "head_camera_depth_optical_frame";
            
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
            lcm.Publish(topicName, imageMsg);
            
            if (debugMode)
                Debug.Log($"Published depth image ({width}x{height}) to topic: {topicName}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error publishing depth to LCM: {ex.Message}");
            Debug.LogException(ex);
        }
    }
    
    void OnDisable()
    {
        // Reset depth texture mode if needed
        if (targetCamera != null)
        {
            // Optional: You might want to keep depth texture enabled for other effects
            // targetCamera.depthTextureMode = DepthTextureMode.None;
        }
    }
}