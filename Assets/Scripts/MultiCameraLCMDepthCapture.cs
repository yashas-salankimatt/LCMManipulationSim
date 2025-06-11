using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using LCM.LCM;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Unity.Collections;
using GrayWolf.GPUInstancing.Domain;

[System.Serializable]
public class CameraDepthSettings
{
    [Header("Camera")]
    public Camera camera;
    
    [Header("LCM Topic Names")]
    public string depthTopicName = "camera_depth#sensor_msgs.Image";
    public string rgbTopicName = "camera_rgb#sensor_msgs.Image";
    public string cameraInfoTopicName = "camera_info#sensor_msgs.CameraInfo";
    
    [Header("Frame IDs")]
    public string depthFrameID = "camera_depth_optical_frame";
    public string rgbFrameID = "camera_rgb_optical_frame";
    public string cameraInfoFrameID = "camera_optical_frame";
    
    [Header("Capture Settings")]
    public bool enabled = true;
    public bool captureDepth = true;
    public bool captureRGB = true;
    public bool publishCameraInfo = true;
    
    [Header("Controls")]
    public KeyCode captureKey = KeyCode.C;
    public bool continuousMode = false;
    public float publishRate = 10f; // Hz (for continuous mode)
    
    [Header("RGB Settings")]
    public int rgbWidth = 640;
    public int rgbHeight = 480;
    public bool useNativeResolution = true;
    
    [Header("Performance Settings")]
    public int downsampleFactor = 1; // 1 = full res, 2 = half res, etc.
    public bool useAsyncCapture = true;
    
    [HideInInspector] public bool captureRequested = false;
    [HideInInspector] public float nextCaptureTime = 0f;
    [HideInInspector] public int cameraInstanceID;
    [HideInInspector] public uint sequenceNumber = 0;
}

public class MultiCameraLCMDepthCapture : MonoBehaviour
{
    [Header("Global Settings")]
    [SerializeField] private bool debugMode = true;
    
    [Header("LCM Settings")]
    [SerializeField] private string lcmURL = "udpm://239.255.76.67:7667?ttl=1";
    [SerializeField] private string depthEncoding = "32FC1";
    [SerializeField] private string rgbEncoding = "rgb8";
    
    [Header("Camera Configurations")]
    [SerializeField] private List<CameraDepthSettings> cameraSettings = new List<CameraDepthSettings>();
    
    [Header("Global Controls")]
    [SerializeField] private KeyCode captureAllKey = KeyCode.Space;
    [SerializeField] private bool globalContinuousMode = false;
    [SerializeField] private float globalPublishRate = 10f;
    
    [Header("Performance")]
    [SerializeField] private bool useBackgroundThread = true;
    [SerializeField] private int maxQueueSize = 10;
    [SerializeField] private bool skipFramesWhenBehind = true;
    
    private LCM.LCM.LCM lcm;
    private Dictionary<int, RenderTexture> cameraRenderTextures = new Dictionary<int, RenderTexture>();
    private Dictionary<int, AsyncGPUReadbackRequest> pendingDepthRequests = new Dictionary<int, AsyncGPUReadbackRequest>();
    private Dictionary<int, AsyncGPUReadbackRequest> pendingRGBRequests = new Dictionary<int, AsyncGPUReadbackRequest>();
    
    // Background thread for LCM publishing
    private Thread publishThread;
    private ConcurrentQueue<PublishData> publishQueue = new ConcurrentQueue<PublishData>();
    private bool isRunning = true;
    
    // Object pooling for memory efficiency
    private Queue<byte[]> byteArrayPool = new Queue<byte[]>();
    private Queue<float[]> floatArrayPool = new Queue<float[]>();
    
    private class PublishData
    {
        public enum DataType { Depth, RGB, CameraInfo }
        public DataType Type;
        public byte[] Data;
        public float[] DepthData;
        public int Width;
        public int Height;
        public CameraDepthSettings Settings;
        public sensor_msgs.CameraInfo CameraInfo;
    }
    
    void Start()
    {
        // Auto-detect cameras if none configured
        if (cameraSettings.Count == 0)
        {
            Camera[] cameras = FindObjectsOfType<Camera>();
            foreach (Camera cam in cameras)
            {
                if (cam.gameObject.activeInHierarchy)
                {
                    CameraDepthSettings settings = new CameraDepthSettings();
                    settings.camera = cam;
                    string baseName = cam.name.ToLower().Replace(" ", "_");
                    settings.depthTopicName = $"{baseName}_depth#sensor_msgs.Image";
                    settings.rgbTopicName = $"{baseName}_rgb#sensor_msgs.Image";
                    settings.cameraInfoTopicName = $"{baseName}_camera_info#sensor_msgs.CameraInfo";
                    settings.depthFrameID = $"{baseName}_depth_optical_frame";
                    settings.rgbFrameID = $"{baseName}_rgb_optical_frame";
                    settings.cameraInfoFrameID = $"{baseName}_optical_frame";
                    cameraSettings.Add(settings);
                }
            }
        }
        
        // Initialize camera settings
        foreach (var settings in cameraSettings)
        {
            if (settings.camera == null) continue;
            
            settings.cameraInstanceID = settings.camera.GetInstanceID();
            
            // Enable depth texture
            if (settings.captureDepth)
            {
                settings.camera.depthTextureMode = DepthTextureMode.Depth;
            }
            
            // Subscribe to render pipeline callbacks for RGB capture (avoids Camera.Render())
            if (settings.captureRGB)
            {
                SetupCameraRenderTexture(settings);
            }
            
            if (debugMode)
            {
                Debug.Log($"Camera configured: {settings.camera.name}");
                Debug.Log($"  Async capture: {settings.useAsyncCapture}");
                Debug.Log($"  Downsample factor: {settings.downsampleFactor}");
            }
        }
        
        try
        {
            lcm = new LCM.LCM.LCM(lcmURL);
            
            // Start background publishing thread
            if (useBackgroundThread)
            {
                publishThread = new Thread(PublishThreadWorker);
                publishThread.Start();
            }
            
            // Subscribe to render pipeline events for RGB capture
            RenderPipelineManager.endCameraRendering += OnCameraRendering;
            
            if (debugMode)
            {
                Debug.Log($"LCM initialized with URL: {lcmURL}");
                Debug.Log($"Background thread: {useBackgroundThread}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error during LCM initialization: " + ex.Message);
        }
    }
    
    void SetupCameraRenderTexture(CameraDepthSettings settings)
    {
        int width = settings.useNativeResolution ? settings.camera.pixelWidth : settings.rgbWidth;
        int height = settings.useNativeResolution ? settings.camera.pixelHeight : settings.rgbHeight;
        
        // Apply downsample factor
        width /= settings.downsampleFactor;
        height /= settings.downsampleFactor;
        
        if (width <= 0 || height <= 0)
        {
            width = settings.rgbWidth / settings.downsampleFactor;
            height = settings.rgbHeight / settings.downsampleFactor;
        }
        
        // Create with ARGB32 format for consistent handling
        RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        rt.name = $"RGB_Capture_{settings.camera.name}";
        rt.Create();
        
        cameraRenderTextures[settings.cameraInstanceID] = rt;
        
        if (debugMode)
            Debug.Log($"Created RGB render texture for {settings.camera.name}: {width}x{height} (ARGB32 format)");
    }
    
    void OnCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        // Find settings for this camera
        CameraDepthSettings settings = null;
        foreach (var s in cameraSettings)
        {
            if (s.camera == camera && s.enabled && s.captureRGB)
            {
                settings = s;
                break;
            }
        }
        
        if (settings == null) return;
        
        // Check if we should capture this frame
        bool shouldCapture = false;
        
        if (globalContinuousMode || settings.continuousMode)
        {
            if (Time.time >= settings.nextCaptureTime)
            {
                float rate = globalContinuousMode ? globalPublishRate : settings.publishRate;
                settings.nextCaptureTime = Time.time + (1f / rate);
                shouldCapture = true;
            }
        }
        
        if (settings.captureRequested)
        {
            shouldCapture = true;
            settings.captureRequested = false;
        }
        
        if (!shouldCapture) return;
        
        // Check if we should skip this frame due to queue backup
        if (skipFramesWhenBehind && publishQueue.Count > maxQueueSize)
        {
            if (debugMode)
                Debug.LogWarning($"Skipping frame for {camera.name} - queue full ({publishQueue.Count} items)");
            return;
        }
        
        // Capture RGB using the camera's current render (no extra Camera.Render() call!)
        if (cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture rt))
        {
            // Blit current camera render to our capture texture
            var cmd = CommandBufferPool.Get("CaptureRGB");
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, rt);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            
            // Request async readback if not already pending
            if (!pendingRGBRequests.ContainsKey(settings.cameraInstanceID) || 
                pendingRGBRequests[settings.cameraInstanceID].done)
            {
                if (settings.useAsyncCapture)
                {
                    // Don't specify format - let it use the native format of the RenderTexture
                    var request = AsyncGPUReadback.Request(rt, 0, 
                        (AsyncGPUReadbackRequest r) => OnRGBReadbackComplete(r, settings));
                    pendingRGBRequests[settings.cameraInstanceID] = request;
                }
                else
                {
                    // Fallback to synchronous capture
                    CaptureRGBSynchronous(rt, settings);
                }
            }
        }
    }
    
    void Update()
    {
        if (lcm == null) return;
        
        // Check for capture keys
        if (Input.GetKeyDown(captureAllKey))
        {
            foreach (var settings in cameraSettings)
            {
                if (settings.enabled && settings.camera != null)
                {
                    settings.captureRequested = true;
                }
            }
        }
        
        // Check individual camera keys
        foreach (var settings in cameraSettings)
        {
            if (!settings.enabled || settings.camera == null) continue;
            
            if (Input.GetKeyDown(settings.captureKey))
            {
                settings.captureRequested = true;
            }
            
            // Handle depth capture in Update (since we need to check for persistent depth texture)
            if (settings.captureDepth)
            {
                bool shouldCaptureDepth = false;
                
                if (globalContinuousMode || settings.continuousMode)
                {
                    if (Time.time >= settings.nextCaptureTime)
                    {
                        shouldCaptureDepth = true;
                    }
                }
                
                if (settings.captureRequested)
                {
                    shouldCaptureDepth = true;
                }
                
                if (shouldCaptureDepth)
                {
                    CaptureDepthForCamera(settings);
                }
            }
        }
        
        // Process synchronous publishing if not using background thread
        if (!useBackgroundThread)
        {
            ProcessPublishQueue();
        }
    }
    
    private void CaptureDepthForCamera(CameraDepthSettings settings)
    {
        // Skip if queue is full
        if (skipFramesWhenBehind && publishQueue.Count > maxQueueSize) return;
        
        RTHandle depthTexture = MultiCameraPersistentDepthFeature.GetPersistentDepthTexture(settings.cameraInstanceID);
        
        if (depthTexture == null || depthTexture.rt == null)
        {
            if (debugMode)
                Debug.LogWarning($"Persistent depth texture not available for camera: {settings.camera.name}");
            return;
        }
        
        // Check if async request is already pending
        if (pendingDepthRequests.ContainsKey(settings.cameraInstanceID) && 
            !pendingDepthRequests[settings.cameraInstanceID].done)
        {
            return; // Skip this frame
        }
        
        Texture sourceTexture = depthTexture.rt;
        
        if (settings.useAsyncCapture)
        {
            // Create temporary RT for async readback if downsampling
            if (settings.downsampleFactor > 1)
            {
                int width = sourceTexture.width / settings.downsampleFactor;
                int height = sourceTexture.height / settings.downsampleFactor;
                RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RFloat);
                Graphics.Blit(sourceTexture, tempRT);
                
                var request = AsyncGPUReadback.Request(tempRT, 0,
                    (AsyncGPUReadbackRequest r) => {
                        RenderTexture.ReleaseTemporary(tempRT);
                        OnDepthReadbackComplete(r, settings);
                    });
                pendingDepthRequests[settings.cameraInstanceID] = request;
            }
            else
            {
                var request = AsyncGPUReadback.Request(sourceTexture, 0,
                    (AsyncGPUReadbackRequest r) => OnDepthReadbackComplete(r, settings));
                pendingDepthRequests[settings.cameraInstanceID] = request;
            }
        }
        else
        {
            CaptureDepthSynchronous(sourceTexture, settings);
        }
        
        // Publish camera info (lightweight, can do immediately)
        if (settings.publishCameraInfo)
        {
            PublishCameraInfo(settings);
        }
        
        // Increment sequence number
        settings.sequenceNumber++;
    }
    
    private void OnDepthReadbackComplete(AsyncGPUReadbackRequest request, CameraDepthSettings settings)
    {
        if (request.hasError)
        {
            Debug.LogError($"Async depth readback failed for camera {settings.camera.name}");
            return;
        }
        
        try
        {
            var data = request.GetData<float>();
            int width = request.width;
            int height = request.height;
            int expectedLength = width * height;
            int actualLength = data.Length;
            
            if (debugMode && actualLength != expectedLength)
            {
                Debug.LogWarning($"Depth data length mismatch for {settings.camera.name}: expected {expectedLength}, got {actualLength}");
            }
            
            // Get pooled array that matches the expected size
            float[] depthArray = GetPooledFloatArray(expectedLength);
            
            // Copy data safely
            int copyLength = Math.Min(actualLength, expectedLength);
            for (int i = 0; i < copyLength; i++)
            {
                depthArray[i] = data[i];
            }
            
            // Fill any remaining with far plane value if necessary
            if (copyLength < expectedLength)
            {
                float farValue = settings.camera.farClipPlane;
                for (int i = copyLength; i < expectedLength; i++)
                {
                    depthArray[i] = 1.0f; // Will be linearized to far plane
                }
            }
            
            // Linearize depth values
            LinearizeDepthValuesInPlace(depthArray, settings.camera);
            
            // Queue for publishing
            var publishData = new PublishData
            {
                Type = PublishData.DataType.Depth,
                DepthData = depthArray,
                Width = width,
                Height = height,
                Settings = settings
            };
            
            publishQueue.Enqueue(publishData);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error processing depth readback for camera {settings.camera.name}: {ex.Message}");
        }
    }
    
    private void OnRGBReadbackComplete(AsyncGPUReadbackRequest request, CameraDepthSettings settings)
    {
        if (request.hasError)
        {
            Debug.LogError($"Async RGB readback failed for camera {settings.camera.name}");
            return;
        }
        
        int width = request.width;
        int height = request.height;
        byte[] rgbArray = null;
        
        try
        {
            // Get the raw data
            var data = request.GetData<byte>();
            int totalBytes = data.Length;
            int expectedPixels = width * height;
            int bytesPerPixel = totalBytes / expectedPixels;
            
            if (debugMode)
            {
                Debug.Log($"RGB Readback - Width: {width}, Height: {height}, Total bytes: {totalBytes}, Bytes per pixel: {bytesPerPixel}");
            }
            
            if (bytesPerPixel == 3)
            {
                // Already RGB24, just copy
                rgbArray = GetPooledByteArray(totalBytes);
                data.CopyTo(rgbArray);
            }
            else if (bytesPerPixel == 4)
            {
                // ARGB32 or RGBA32, need to convert to RGB24
                rgbArray = GetPooledByteArray(width * height * 3);
                int srcIndex = 0;
                int dstIndex = 0;
                
                // Check if it's ARGB or RGBA by examining a few pixels
                // In Unity, it's typically RGBA32 format
                for (int i = 0; i < expectedPixels; i++)
                {
                    // RGBA32 format: R, G, B, A
                    rgbArray[dstIndex++] = data[srcIndex];     // R
                    rgbArray[dstIndex++] = data[srcIndex + 1]; // G
                    rgbArray[dstIndex++] = data[srcIndex + 2]; // B
                    srcIndex += 4; // Skip alpha
                }
            }
            else
            {
                // Unexpected format - try to handle gracefully
                Debug.LogError($"Unexpected bytes per pixel: {bytesPerPixel} for camera {settings.camera.name}. Total bytes: {totalBytes}, Expected pixels: {expectedPixels}");
                
                // Try to salvage what we can
                rgbArray = GetPooledByteArray(width * height * 3);
                int copyLength = Math.Min(totalBytes, rgbArray.Length);
                for (int i = 0; i < copyLength; i++)
                {
                    rgbArray[i] = data[i];
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error processing RGB readback for camera {settings.camera.name}: {ex.Message}");
            return;
        }
        
        // Queue for publishing
        var publishData = new PublishData
        {
            Type = PublishData.DataType.RGB,
            Data = rgbArray,
            Width = width,
            Height = height,
            Settings = settings
        };
        
        publishQueue.Enqueue(publishData);
    }
    
    private void CaptureDepthSynchronous(Texture sourceTexture, CameraDepthSettings settings)
    {
        int width = sourceTexture.width / settings.downsampleFactor;
        int height = sourceTexture.height / settings.downsampleFactor;
        
        RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RFloat);
        Graphics.Blit(sourceTexture, tempRT);
        
        RenderTexture.active = tempRT;
        Texture2D depthImage = new Texture2D(width, height, TextureFormat.RFloat, false);
        depthImage.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        depthImage.Apply();
        RenderTexture.active = null;
        
        Color[] rawDepthPixels = depthImage.GetPixels();
        float[] linearDepthValues = LinearizeDepthValues(rawDepthPixels, settings.camera);
        
        var publishData = new PublishData
        {
            Type = PublishData.DataType.Depth,
            DepthData = linearDepthValues,
            Width = width,
            Height = height,
            Settings = settings
        };
        
        if (useBackgroundThread)
            publishQueue.Enqueue(publishData);
        else
            PublishDepthToLCM(publishData);
        
        RenderTexture.ReleaseTemporary(tempRT);
        DestroyImmediate(depthImage);
    }
    
    private void CaptureRGBSynchronous(RenderTexture sourceTexture, CameraDepthSettings settings)
    {
        RenderTexture.active = sourceTexture;
        Texture2D rgbImage = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGB24, false);
        rgbImage.ReadPixels(new Rect(0, 0, sourceTexture.width, sourceTexture.height), 0, 0);
        rgbImage.Apply();
        RenderTexture.active = null;
        
        byte[] rgbData = rgbImage.GetRawTextureData();
        
        var publishData = new PublishData
        {
            Type = PublishData.DataType.RGB,
            Data = rgbData,
            Width = sourceTexture.width,
            Height = sourceTexture.height,
            Settings = settings
        };
        
        if (useBackgroundThread)
            publishQueue.Enqueue(publishData);
        else
            PublishRGBToLCM(publishData);
        
        DestroyImmediate(rgbImage);
    }
    
    private void PublishThreadWorker()
    {
        while (isRunning)
        {
            ProcessPublishQueue();
            Thread.Sleep(1); // Small sleep to prevent CPU spinning
        }
    }
    
    private void ProcessPublishQueue()
    {
        int processed = 0;
        while (publishQueue.TryDequeue(out PublishData data) && processed < 5) // Process up to 5 items per frame
        {
            try
            {
                switch (data.Type)
                {
                    case PublishData.DataType.Depth:
                        PublishDepthToLCM(data);
                        break;
                    case PublishData.DataType.RGB:
                        PublishRGBToLCM(data);
                        break;
                    case PublishData.DataType.CameraInfo:
                        lcm.Publish(data.Settings.cameraInfoTopicName, data.CameraInfo);
                        break;
                }
                processed++;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error publishing data: {ex.Message}");
            }
            finally
            {
                // Return arrays to pool
                if (data.Data != null)
                    ReturnByteArrayToPool(data.Data);
                if (data.DepthData != null)
                    ReturnFloatArrayToPool(data.DepthData);
            }
        }
    }
    
    private void LinearizeDepthValuesInPlace(float[] depthValues, Camera camera)
    {
        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        
        for (int i = 0; i < depthValues.Length; i++)
        {
            depthValues[i] = CorrectPerspectiveLinearization(depthValues[i], near, far);
        }
    }
    
    private float[] LinearizeDepthValues(Color[] rawDepthPixels, Camera camera)
    {
        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        float[] linearDepths = GetPooledFloatArray(rawDepthPixels.Length);
        
        for (int i = 0; i < rawDepthPixels.Length; i++)
        {
            linearDepths[i] = CorrectPerspectiveLinearization(rawDepthPixels[i].r, near, far);
        }
        
        return linearDepths;
    }
    
    private float CorrectPerspectiveLinearization(float rawDepth, float near, float far)
    {
        float depth01 = SystemInfo.usesReversedZBuffer ? (1.0f - rawDepth) : rawDepth;
        
        if (depth01 <= 0.0001f) return near;
        if (depth01 >= 0.9999f) return far;
        
        float linearDepth = (near * far) / (far - depth01 * (far - near));
        return Mathf.Clamp(linearDepth, near, far * 2.0f);
    }
    
    private void PublishDepthToLCM(PublishData data)
    {
        var imageMsg = new sensor_msgs.Image();
        imageMsg.header = CreateHeader(data.Settings.sequenceNumber, data.Settings.depthFrameID);
        imageMsg.height = data.Height;
        imageMsg.width = data.Width;
        imageMsg.encoding = depthEncoding;
        imageMsg.is_bigendian = BitConverter.IsLittleEndian ? (byte)0 : (byte)1;
        imageMsg.step = data.Width * sizeof(float);
        
        int dataSize = data.DepthData.Length * sizeof(float);
        imageMsg.data = GetPooledByteArray(dataSize);
        imageMsg.data_length = dataSize;
        
        Buffer.BlockCopy(data.DepthData, 0, imageMsg.data, 0, dataSize);
        
        lcm.Publish(data.Settings.depthTopicName, imageMsg);
        
        // Return byte array to pool after publishing
        ReturnByteArrayToPool(imageMsg.data);
    }
    
    private void PublishRGBToLCM(PublishData data)
    {
        var imageMsg = new sensor_msgs.Image();
        imageMsg.header = CreateHeader(data.Settings.sequenceNumber, data.Settings.rgbFrameID);
        imageMsg.height = data.Height;
        imageMsg.width = data.Width;
        imageMsg.encoding = rgbEncoding;
        imageMsg.is_bigendian = BitConverter.IsLittleEndian ? (byte)0 : (byte)1;
        imageMsg.step = data.Width * 3;
        imageMsg.data = data.Data;
        imageMsg.data_length = data.Data.Length;
        
        lcm.Publish(data.Settings.rgbTopicName, imageMsg);
    }
    
    private void PublishCameraInfo(CameraDepthSettings settings)
    {
        var cameraInfoMsg = CreateCameraInfoMessage(settings);
        
        if (useBackgroundThread)
        {
            var publishData = new PublishData
            {
                Type = PublishData.DataType.CameraInfo,
                CameraInfo = cameraInfoMsg,
                Settings = settings
            };
            publishQueue.Enqueue(publishData);
        }
        else
        {
            lcm.Publish(settings.cameraInfoTopicName, cameraInfoMsg);
        }
    }
    
    private sensor_msgs.CameraInfo CreateCameraInfoMessage(CameraDepthSettings settings)
    {
        var cameraInfoMsg = new sensor_msgs.CameraInfo();
        cameraInfoMsg.header = CreateHeader(settings.sequenceNumber, settings.cameraInfoFrameID);
        
        int width, height;
        if (settings.captureRGB && cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture rt))
        {
            width = rt.width;
            height = rt.height;
        }
        else
        {
            width = (settings.camera.pixelWidth > 0 ? settings.camera.pixelWidth : settings.rgbWidth) / settings.downsampleFactor;
            height = (settings.camera.pixelHeight > 0 ? settings.camera.pixelHeight : settings.rgbHeight) / settings.downsampleFactor;
        }
        
        cameraInfoMsg.height = height;
        cameraInfoMsg.width = width;
        cameraInfoMsg.distortion_model = "plumb_bob";
        cameraInfoMsg.D_length = 5;
        cameraInfoMsg.D = new double[5] { 0.0, 0.0, 0.0, 0.0, 0.0 };
        
        float fovY = settings.camera.fieldOfView * Mathf.Deg2Rad;
        float fovX = 2.0f * Mathf.Atan(Mathf.Tan(fovY * 0.5f) * settings.camera.aspect);
        float fy = height / (2.0f * Mathf.Tan(fovY * 0.5f));
        float fx = width / (2.0f * Mathf.Tan(fovX * 0.5f));
        float cx = width * 0.5f;
        float cy = height * 0.5f;
        
        // Set K matrix
        cameraInfoMsg.K[0] = fx; cameraInfoMsg.K[1] = 0;  cameraInfoMsg.K[2] = cx;
        cameraInfoMsg.K[3] = 0;  cameraInfoMsg.K[4] = fy; cameraInfoMsg.K[5] = cy;
        cameraInfoMsg.K[6] = 0;  cameraInfoMsg.K[7] = 0;  cameraInfoMsg.K[8] = 1;
        
        // Set R matrix (identity)
        cameraInfoMsg.R[0] = 1; cameraInfoMsg.R[1] = 0; cameraInfoMsg.R[2] = 0;
        cameraInfoMsg.R[3] = 0; cameraInfoMsg.R[4] = 1; cameraInfoMsg.R[5] = 0;
        cameraInfoMsg.R[6] = 0; cameraInfoMsg.R[7] = 0; cameraInfoMsg.R[8] = 1;
        
        // Set P matrix
        cameraInfoMsg.P[0] = fx; cameraInfoMsg.P[1] = 0;  cameraInfoMsg.P[2] = cx; cameraInfoMsg.P[3] = 0;
        cameraInfoMsg.P[4] = 0;  cameraInfoMsg.P[5] = fy; cameraInfoMsg.P[6] = cy; cameraInfoMsg.P[7] = 0;
        cameraInfoMsg.P[8] = 0;  cameraInfoMsg.P[9] = 0;  cameraInfoMsg.P[10] = 1; cameraInfoMsg.P[11] = 0;
        
        cameraInfoMsg.binning_x = 0;
        cameraInfoMsg.binning_y = 0;
        
        cameraInfoMsg.roi = new sensor_msgs.RegionOfInterest();
        cameraInfoMsg.roi.x_offset = 0;
        cameraInfoMsg.roi.y_offset = 0;
        cameraInfoMsg.roi.height = height;
        cameraInfoMsg.roi.width = width;
        cameraInfoMsg.roi.do_rectify = false;
        
        return cameraInfoMsg;
    }
    
    private std_msgs.Header CreateHeader(uint sequenceNumber, string frameId)
    {
        var header = new std_msgs.Header();
        header.seq = (int)sequenceNumber;
        header.stamp = new std_msgs.Time();
        header.stamp.sec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        header.stamp.nsec = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000) * 1000000);
        header.frame_id = frameId;
        return header;
    }
    
    // Object pooling methods
    private byte[] GetPooledByteArray(int size)
    {
        lock (byteArrayPool)
        {
            if (byteArrayPool.Count > 0)
            {
                var array = byteArrayPool.Dequeue();
                if (array.Length >= size)
                    return array;
            }
            return new byte[size];
        }
    }
    
    private float[] GetPooledFloatArray(int size)
    {
        lock (floatArrayPool)
        {
            if (floatArrayPool.Count > 0)
            {
                var array = floatArrayPool.Dequeue();
                if (array.Length >= size)
                    return array;
            }
            return new float[size];
        }
    }
    
    private void ReturnByteArrayToPool(byte[] array)
    {
        if (array == null) return;
        lock (byteArrayPool)
        {
            if (byteArrayPool.Count < 20) // Keep pool size reasonable
                byteArrayPool.Enqueue(array);
        }
    }
    
    private void ReturnFloatArrayToPool(float[] array)
    {
        if (array == null) return;
        lock (floatArrayPool)
        {
            if (floatArrayPool.Count < 20) // Keep pool size reasonable
                floatArrayPool.Enqueue(array);
        }
    }
    
    void OnDestroy()
    {
        isRunning = false;
        
        if (publishThread != null)
        {
            publishThread.Join(1000);
        }
        
        RenderPipelineManager.endCameraRendering -= OnCameraRendering;
        
        foreach (var settings in cameraSettings)
        {
            if (settings.camera != null)
            {
                MultiCameraPersistentDepthFeature.CleanupCameraDepthTexture(settings.cameraInstanceID);
            }
        }
        
        foreach (var kvp in cameraRenderTextures)
        {
            if (kvp.Value != null)
            {
                kvp.Value.Release();
                DestroyImmediate(kvp.Value);
            }
        }
        cameraRenderTextures.Clear();
    }
    
    // Public API methods
    public void AddCamera(Camera camera, string depthTopic = null, string rgbTopic = null, string cameraInfoTopic = null)
    {
        if (camera == null) return;
        
        // Check if camera already exists
        foreach (var existing in cameraSettings)
        {
            if (existing.camera == camera) return;
        }
        
        CameraDepthSettings newSettings = new CameraDepthSettings();
        newSettings.camera = camera;
        newSettings.cameraInstanceID = camera.GetInstanceID();
        
        string baseName = camera.name.ToLower().Replace(" ", "_");
        newSettings.depthTopicName = depthTopic ?? $"{baseName}_depth#sensor_msgs.Image";
        newSettings.rgbTopicName = rgbTopic ?? $"{baseName}_rgb#sensor_msgs.Image";
        newSettings.cameraInfoTopicName = cameraInfoTopic ?? $"{baseName}_camera_info#sensor_msgs.CameraInfo";
        newSettings.depthFrameID = $"{baseName}_depth_optical_frame";
        newSettings.rgbFrameID = $"{baseName}_rgb_optical_frame";
        newSettings.cameraInfoFrameID = $"{baseName}_optical_frame";
        
        // Enable depth texture
        if (newSettings.captureDepth)
        {
            camera.depthTextureMode = DepthTextureMode.Depth;
        }
        
        // Setup RGB capture
        if (newSettings.captureRGB)
        {
            SetupCameraRenderTexture(newSettings);
        }
        
        cameraSettings.Add(newSettings);
        
        if (debugMode)
            Debug.Log($"Added camera {camera.name} for capture");
    }
    
    public void RemoveCamera(Camera camera)
    {
        if (camera == null) return;
        
        for (int i = cameraSettings.Count - 1; i >= 0; i--)
        {
            if (cameraSettings[i].camera == camera)
            {
                var settings = cameraSettings[i];
                
                // Clean up depth texture
                MultiCameraPersistentDepthFeature.CleanupCameraDepthTexture(settings.cameraInstanceID);
                
                // Clean up RGB render texture
                if (cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture rt))
                {
                    if (rt != null)
                    {
                        rt.Release();
                        DestroyImmediate(rt);
                    }
                    cameraRenderTextures.Remove(settings.cameraInstanceID);
                }
                
                cameraSettings.RemoveAt(i);
                
                if (debugMode)
                    Debug.Log($"Removed camera {camera.name} from capture");
                break;
            }
        }
    }
    
    public void CaptureForCamera(Camera camera)
    {
        foreach (var settings in cameraSettings)
        {
            if (settings.camera == camera && settings.enabled)
            {
                settings.captureRequested = true;
                break;
            }
        }
    }
    
    public void CaptureForAllCameras()
    {
        foreach (var settings in cameraSettings)
        {
            if (settings.enabled && settings.camera != null)
            {
                settings.captureRequested = true;
            }
        }
    }
}