using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using LCM.LCM;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using GrayWolf.GPUInstancing.Domain;
using System.Diagnostics;

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

// Performance tracking class
public class PerformanceTracker
{
    private struct TimingSample
    {
        public double time;
        public int count;
    }
    
    private Dictionary<string, List<double>> timings = new Dictionary<string, List<double>>();
    private Dictionary<string, int> counts = new Dictionary<string, int>();
    private readonly object lockObject = new object();
    
    public void RecordTime(string operation, double milliseconds)
    {
        lock (lockObject)
        {
            if (!timings.ContainsKey(operation))
            {
                timings[operation] = new List<double>();
                counts[operation] = 0;
            }
            
            timings[operation].Add(milliseconds);
            counts[operation]++;
            
            // Keep only last 100 samples to prevent memory growth
            if (timings[operation].Count > 100)
            {
                timings[operation].RemoveAt(0);
            }
        }
    }
    
    public string GetSummary()
    {
        lock (lockObject)
        {
            var summary = new System.Text.StringBuilder();
            summary.AppendLine("=== PERFORMANCE SUMMARY ===");
            
            double totalTime = 0;
            int totalOperations = 0;
            
            foreach (var kvp in timings)
            {
                string operation = kvp.Key;
                var times = kvp.Value;
                int count = counts[operation];
                
                if (times.Count == 0) continue;
                
                double sum = 0;
                double min = double.MaxValue;
                double max = double.MinValue;
                
                foreach (double time in times)
                {
                    sum += time;
                    if (time < min) min = time;
                    if (time > max) max = time;
                }
                
                double avg = sum / times.Count;
                totalTime += sum;
                totalOperations += times.Count;
                
                summary.AppendLine($"{operation}: Avg={avg:F2}ms, Min={min:F2}ms, Max={max:F2}ms, Count={count}, Total={sum:F1}ms");
            }
            
            if (totalOperations > 0)
            {
                summary.AppendLine($"OVERALL: Total={totalTime:F1}ms, Operations={totalOperations}, Avg per op={totalTime/totalOperations:F2}ms");
            }
            
            return summary.ToString();
        }
    }
    
    public void Reset()
    {
        lock (lockObject)
        {
            timings.Clear();
            counts.Clear();
        }
    }
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
    
    [Header("Enhanced Performance")]
    [SerializeField] private int numPublishThreads = 3; // Multiple publisher threads
    [SerializeField] private int maxQueueSize = 20; // Increased queue size
    [SerializeField] private bool skipFramesWhenBehind = true;
    [SerializeField] private bool useOptimizedRGBConversion = true;
    
    [Header("Performance Monitoring")]
    [SerializeField] private bool enablePerformanceTracking = true;
    [SerializeField] private float performanceLogInterval = 5f; // Log summary every N seconds
    
    private LCM.LCM.LCM lcm;
    private Dictionary<int, RenderTexture> cameraRenderTextures = new Dictionary<int, RenderTexture>();
    private Dictionary<int, AsyncGPUReadbackRequest> pendingDepthRequests = new Dictionary<int, AsyncGPUReadbackRequest>();
    private Dictionary<int, AsyncGPUReadbackRequest> pendingRGBRequests = new Dictionary<int, AsyncGPUReadbackRequest>();
    
    // Multiple publishing threads for better throughput
    private Thread[] publishThreads;
    private ConcurrentQueue<PublishData> publishQueue = new ConcurrentQueue<PublishData>();
    private volatile bool isRunning = true;
    
    // Enhanced object pooling with larger pools
    private ConcurrentQueue<byte[]> byteArrayPool = new ConcurrentQueue<byte[]>();
    private ConcurrentQueue<float[]> floatArrayPool = new ConcurrentQueue<float[]>();
    
    // Performance tracking
    private PerformanceTracker performanceTracker = new PerformanceTracker();
    private float lastPerformanceLogTime = 0f;
    
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
        public double CaptureStartTime; // For end-to-end timing
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
            
            // Subscribe to render pipeline callbacks for RGB capture
            if (settings.captureRGB)
            {
                SetupCameraRenderTexture(settings);
            }
            
            if (debugMode)
            {
                UnityEngine.Debug.Log($"Camera configured: {settings.camera.name}");
                UnityEngine.Debug.Log($"  Async capture: {settings.useAsyncCapture}");
                UnityEngine.Debug.Log($"  Downsample factor: {settings.downsampleFactor}");
            }
        }
        
        try
        {
            lcm = new LCM.LCM.LCM(lcmURL);
            
            // Start multiple publishing threads for better throughput
            publishThreads = new Thread[numPublishThreads];
            for (int i = 0; i < numPublishThreads; i++)
            {
                publishThreads[i] = new Thread(() => PublishThreadWorker($"Publisher-{i}"));
                publishThreads[i].Name = $"LCM-Publisher-{i}";
                publishThreads[i].Start();
            }
            
            // Subscribe to render pipeline events for RGB capture
            RenderPipelineManager.endCameraRendering += OnCameraRendering;
            
            if (debugMode)
            {
                UnityEngine.Debug.Log($"LCM initialized with URL: {lcmURL}");
                UnityEngine.Debug.Log($"Publisher threads: {numPublishThreads}");
                UnityEngine.Debug.Log($"Optimized RGB conversion: {useOptimizedRGBConversion}");
                UnityEngine.Debug.Log($"Performance tracking: {enablePerformanceTracking}");
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("Error during LCM initialization: " + ex.Message);
        }
    }
    
    void SetupCameraRenderTexture(CameraDepthSettings settings)
    {
        var sw = Stopwatch.StartNew();
        
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
        
        sw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("Setup_RenderTexture", sw.Elapsed.TotalMilliseconds);
        
        if (debugMode)
            UnityEngine.Debug.Log($"Created RGB render texture for {settings.camera.name}: {width}x{height} (ARGB32 format)");
    }
    
    void OnCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        var totalSw = Stopwatch.StartNew();
        
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
                UnityEngine.Debug.LogWarning($"Skipping frame for {camera.name} - queue full ({publishQueue.Count} items)");
            return;
        }
        
        // Capture RGB using the camera's current render
        if (cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture rt))
        {
            var blitSw = Stopwatch.StartNew();
            
            // Blit current camera render to our capture texture
            var cmd = CommandBufferPool.Get("CaptureRGB");
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, rt);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            
            blitSw.Stop();
            if (enablePerformanceTracking)
                performanceTracker.RecordTime("Camera_Blit", blitSw.Elapsed.TotalMilliseconds);
            
            // Request async readback if not already pending
            if (!pendingRGBRequests.ContainsKey(settings.cameraInstanceID) || 
                pendingRGBRequests[settings.cameraInstanceID].done)
            {
                double captureStartTime = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
                
                if (settings.useAsyncCapture)
                {
                    var request = AsyncGPUReadback.Request(rt, 0, 
                        (AsyncGPUReadbackRequest r) => OnRGBReadbackComplete(r, settings, captureStartTime));
                    pendingRGBRequests[settings.cameraInstanceID] = request;
                }
                else
                {
                    CaptureRGBSynchronous(rt, settings, captureStartTime);
                }
            }
        }
        
        totalSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("OnCameraRendering_Total", totalSw.Elapsed.TotalMilliseconds);
    }
    
    void Update()
    {
        if (lcm == null) return;
        
        // Log performance summary periodically
        if (enablePerformanceTracking && Time.time - lastPerformanceLogTime >= performanceLogInterval)
        {
            UnityEngine.Debug.Log(performanceTracker.GetSummary());
            performanceTracker.Reset();
            lastPerformanceLogTime = Time.time;
        }
        
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
            
            // Handle depth capture in Update
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
    }
    
    private void CaptureDepthForCamera(CameraDepthSettings settings)
    {
        var totalSw = Stopwatch.StartNew();
        
        // Skip if queue is full
        if (skipFramesWhenBehind && publishQueue.Count > maxQueueSize) return;
        
        var textureSw = Stopwatch.StartNew();
        RTHandle depthTexture = MultiCameraPersistentDepthFeature.GetPersistentDepthTexture(settings.cameraInstanceID);
        textureSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("Get_DepthTexture", textureSw.Elapsed.TotalMilliseconds);
        
        if (depthTexture == null || depthTexture.rt == null)
        {
            if (debugMode)
                UnityEngine.Debug.LogWarning($"Persistent depth texture not available for camera: {settings.camera.name}");
            return;
        }
        
        // Check if async request is already pending
        if (pendingDepthRequests.ContainsKey(settings.cameraInstanceID) && 
            !pendingDepthRequests[settings.cameraInstanceID].done)
        {
            return; // Skip this frame
        }
        
        Texture sourceTexture = depthTexture.rt;
        double captureStartTime = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        
        if (settings.useAsyncCapture)
        {
            var requestSw = Stopwatch.StartNew();
            
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
                        OnDepthReadbackComplete(r, settings, captureStartTime);
                    });
                pendingDepthRequests[settings.cameraInstanceID] = request;
            }
            else
            {
                var request = AsyncGPUReadback.Request(sourceTexture, 0,
                    (AsyncGPUReadbackRequest r) => OnDepthReadbackComplete(r, settings, captureStartTime));
                pendingDepthRequests[settings.cameraInstanceID] = request;
            }
            
            requestSw.Stop();
            if (enablePerformanceTracking)
                performanceTracker.RecordTime("Depth_AsyncRequest", requestSw.Elapsed.TotalMilliseconds);
        }
        else
        {
            CaptureDepthSynchronous(sourceTexture, settings, captureStartTime);
        }
        
        // Publish camera info (lightweight, can do immediately)
        if (settings.publishCameraInfo)
        {
            PublishCameraInfo(settings);
        }
        
        // Increment sequence number
        settings.sequenceNumber++;
        
        totalSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("CaptureDepthForCamera_Total", totalSw.Elapsed.TotalMilliseconds);
    }
    
    private void OnDepthReadbackComplete(AsyncGPUReadbackRequest request, CameraDepthSettings settings, double captureStartTime)
    {
        var totalSw = Stopwatch.StartNew();
        
        if (request.hasError)
        {
            UnityEngine.Debug.LogError($"Async depth readback failed for camera {settings.camera.name}");
            return;
        }
        
        try
        {
            var copySw = Stopwatch.StartNew();
            var data = request.GetData<float>();
            int width = request.width;
            int height = request.height;
            int expectedLength = width * height;
            int actualLength = data.Length;
            
            // Get pooled array that matches the expected size
            float[] depthArray = GetPooledFloatArray(expectedLength);
            
            // Copy data safely using NativeArray.Copy which is faster
            int copyLength = Math.Min(actualLength, expectedLength);
            NativeArray<float>.Copy(data, depthArray, copyLength);
            
            // Fill any remaining with far plane value if necessary
            if (copyLength < expectedLength)
            {
                for (int i = copyLength; i < expectedLength; i++)
                {
                    depthArray[i] = 1.0f; // Will be linearized to far plane
                }
            }
            copySw.Stop();
            if (enablePerformanceTracking)
                performanceTracker.RecordTime("Depth_DataCopy", copySw.Elapsed.TotalMilliseconds);
            
            // FIXED: Flip depth data vertically to correct Unity's bottom-left origin
            var flipSw = Stopwatch.StartNew();
            FlipDepthDataVertically(depthArray, width, height);
            flipSw.Stop();
            if (enablePerformanceTracking)
                performanceTracker.RecordTime("Depth_VerticalFlip", flipSw.Elapsed.TotalMilliseconds);
            
            // Linearize depth values
            var linearizeSw = Stopwatch.StartNew();
            LinearizeDepthValuesInPlace(depthArray, settings.camera);
            linearizeSw.Stop();
            if (enablePerformanceTracking)
                performanceTracker.RecordTime("Depth_Linearization", linearizeSw.Elapsed.TotalMilliseconds);
            
            // Queue for publishing
            var publishData = new PublishData
            {
                Type = PublishData.DataType.Depth,
                DepthData = depthArray,
                Width = width,
                Height = height,
                Settings = settings,
                CaptureStartTime = captureStartTime
            };
            
            publishQueue.Enqueue(publishData);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Error processing depth readback for camera {settings.camera.name}: {ex.Message}");
        }
        
        totalSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("OnDepthReadbackComplete_Total", totalSw.Elapsed.TotalMilliseconds);
    }
    
    // ADDED: Method to flip depth data vertically
    private void FlipDepthDataVertically(float[] depthData, int width, int height)
    {
        for (int y = 0; y < height / 2; y++)
        {
            int topRowStart = y * width;
            int bottomRowStart = (height - 1 - y) * width;
            
            for (int x = 0; x < width; x++)
            {
                // Swap pixels between top and bottom rows
                float temp = depthData[topRowStart + x];
                depthData[topRowStart + x] = depthData[bottomRowStart + x];
                depthData[bottomRowStart + x] = temp;
            }
        }
    }
    
    private void OnRGBReadbackComplete(AsyncGPUReadbackRequest request, CameraDepthSettings settings, double captureStartTime)
    {
        var totalSw = Stopwatch.StartNew();
        
        if (request.hasError)
        {
            UnityEngine.Debug.LogError($"Async RGB readback failed for camera {settings.camera.name}");
            return;
        }
        
        int width = request.width;
        int height = request.height;
        int pixelCount = width * height;
        
        try
        {
            var dataCopySw = Stopwatch.StartNew();
            var data = request.GetData<byte>();
            int totalBytes = data.Length;
            int bytesPerPixel = totalBytes / pixelCount;
            dataCopySw.Stop();
            if (enablePerformanceTracking)
                performanceTracker.RecordTime("RGB_GetData", dataCopySw.Elapsed.TotalMilliseconds);
            
            if (debugMode && bytesPerPixel != 4)
            {
                UnityEngine.Debug.LogWarning($"Expected 4 bytes per pixel, got {bytesPerPixel} for {settings.camera.name}");
            }
            
            byte[] rgbArray = null;
            
            if (bytesPerPixel == 3)
            {
                var copySw = Stopwatch.StartNew();
                // Already RGB24, just copy
                rgbArray = GetPooledByteArray(totalBytes);
                NativeArray<byte>.Copy(data, rgbArray, totalBytes);
                copySw.Stop();
                if (enablePerformanceTracking)
                    performanceTracker.RecordTime("RGB_DirectCopy", copySw.Elapsed.TotalMilliseconds);
            }
            else if (bytesPerPixel == 4)
            {
                // RGBA32 to RGB24 conversion
                rgbArray = GetPooledByteArray(pixelCount * 3);
                
                var conversionSw = Stopwatch.StartNew();
                if (useOptimizedRGBConversion)
                {
                    // Use optimized unsafe conversion
                    ConvertRGBAToRGBOptimized(data, rgbArray, pixelCount);
                }
                else
                {
                    // Fallback to safe conversion
                    ConvertRGBAToRGBSafe(data, rgbArray, pixelCount);
                }
                conversionSw.Stop();
                if (enablePerformanceTracking)
                    performanceTracker.RecordTime(useOptimizedRGBConversion ? "RGB_OptimizedConversion" : "RGB_SafeConversion", 
                        conversionSw.Elapsed.TotalMilliseconds);
            }
            else
            {
                UnityEngine.Debug.LogError($"Unsupported bytes per pixel: {bytesPerPixel}");
                return;
            }
            
            // Queue for publishing
            var publishData = new PublishData
            {
                Type = PublishData.DataType.RGB,
                Data = rgbArray,
                Width = width,
                Height = height,
                Settings = settings,
                CaptureStartTime = captureStartTime
            };
            
            publishQueue.Enqueue(publishData);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Error processing RGB readback for camera {settings.camera.name}: {ex.Message}");
        }
        
        totalSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("OnRGBReadbackComplete_Total", totalSw.Elapsed.TotalMilliseconds);
    }
    
    private unsafe void ConvertRGBAToRGBOptimized(NativeArray<byte> rgba, byte[] rgb, int pixelCount)
    {
        fixed (byte* rgbPtr = rgb)
        {
            byte* rgbaPtr = (byte*)rgba.GetUnsafeReadOnlyPtr();
            byte* dstPtr = rgbPtr;
            byte* srcPtr = rgbaPtr;
            
            // Process 8 pixels at a time for better vectorization
            int fullGroups = pixelCount / 8;
            int remainder = pixelCount % 8;
            
            for (int i = 0; i < fullGroups; i++)
            {
                // Unroll loop for 8 pixels
                for (int j = 0; j < 8; j++)
                {
                    *dstPtr++ = *srcPtr++;     // R
                    *dstPtr++ = *srcPtr++;     // G
                    *dstPtr++ = *srcPtr++;     // B
                    srcPtr++;                  // Skip A
                }
            }
            
            // Handle remaining pixels
            for (int i = 0; i < remainder; i++)
            {
                *dstPtr++ = *srcPtr++;     // R
                *dstPtr++ = *srcPtr++;     // G
                *dstPtr++ = *srcPtr++;     // B
                srcPtr++;                  // Skip A
            }
        }
    }
    
    private void ConvertRGBAToRGBSafe(NativeArray<byte> rgba, byte[] rgb, int pixelCount)
    {
        int srcIndex = 0;
        int dstIndex = 0;
        
        for (int i = 0; i < pixelCount; i++)
        {
            rgb[dstIndex++] = rgba[srcIndex++];     // R
            rgb[dstIndex++] = rgba[srcIndex++];     // G
            rgb[dstIndex++] = rgba[srcIndex++];     // B
            srcIndex++;                             // Skip A
        }
    }
    
    private void CaptureDepthSynchronous(Texture sourceTexture, CameraDepthSettings settings, double captureStartTime)
    {
        var totalSw = Stopwatch.StartNew();
        
        int width = sourceTexture.width / settings.downsampleFactor;
        int height = sourceTexture.height / settings.downsampleFactor;
        
        var renderSw = Stopwatch.StartNew();
        RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RFloat);
        Graphics.Blit(sourceTexture, tempRT);
        
        RenderTexture.active = tempRT;
        Texture2D depthImage = new Texture2D(width, height, TextureFormat.RFloat, false);
        depthImage.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        depthImage.Apply();
        RenderTexture.active = null;
        renderSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("Depth_SyncRender", renderSw.Elapsed.TotalMilliseconds);
        
        var processSw = Stopwatch.StartNew();
        Color[] rawDepthPixels = depthImage.GetPixels();
        
        // FIXED: Convert Color array to float array and flip vertically
        float[] linearDepthValues = GetPooledFloatArray(rawDepthPixels.Length);
        for (int i = 0; i < rawDepthPixels.Length; i++)
        {
            linearDepthValues[i] = rawDepthPixels[i].r;
        }
        
        // Flip depth data vertically
        FlipDepthDataVertically(linearDepthValues, width, height);
        
        // Linearize depth values
        LinearizeDepthValuesInPlace(linearDepthValues, settings.camera);
        
        processSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("Depth_SyncProcess", processSw.Elapsed.TotalMilliseconds);
        
        var publishData = new PublishData
        {
            Type = PublishData.DataType.Depth,
            DepthData = linearDepthValues,
            Width = width,
            Height = height,
            Settings = settings,
            CaptureStartTime = captureStartTime
        };
        
        publishQueue.Enqueue(publishData);
        
        RenderTexture.ReleaseTemporary(tempRT);
        DestroyImmediate(depthImage);
        
        totalSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("CaptureDepthSynchronous_Total", totalSw.Elapsed.TotalMilliseconds);
    }
    
    private void CaptureRGBSynchronous(RenderTexture sourceTexture, CameraDepthSettings settings, double captureStartTime)
    {
        var totalSw = Stopwatch.StartNew();
        
        var renderSw = Stopwatch.StartNew();
        RenderTexture.active = sourceTexture;
        Texture2D rgbImage = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGB24, false);
        rgbImage.ReadPixels(new Rect(0, 0, sourceTexture.width, sourceTexture.height), 0, 0);
        rgbImage.Apply();
        RenderTexture.active = null;
        renderSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("RGB_SyncRender", renderSw.Elapsed.TotalMilliseconds);
        
        var dataSw = Stopwatch.StartNew();
        byte[] rgbData = rgbImage.GetRawTextureData();
        dataSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("RGB_GetRawData", dataSw.Elapsed.TotalMilliseconds);
        
        var publishData = new PublishData
        {
            Type = PublishData.DataType.RGB,
            Data = rgbData,
            Width = sourceTexture.width,
            Height = sourceTexture.height,
            Settings = settings,
            CaptureStartTime = captureStartTime
        };
        
        publishQueue.Enqueue(publishData);
        
        DestroyImmediate(rgbImage);
        
        totalSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("CaptureRGBSynchronous_Total", totalSw.Elapsed.TotalMilliseconds);
    }
    
    private void PublishThreadWorker(string threadName)
    {
        if (debugMode)
            UnityEngine.Debug.Log($"Started {threadName}");
        
        while (isRunning)
        {
            bool processedAny = false;
            
            // Process items without artificial limits
            while (publishQueue.TryDequeue(out PublishData data))
            {
                var sw = Stopwatch.StartNew();
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
                    
                    sw.Stop();
                    if (enablePerformanceTracking)
                    {
                        performanceTracker.RecordTime($"Publish_{data.Type}", sw.Elapsed.TotalMilliseconds);
                        
                        // Record end-to-end time
                        double endTime = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
                        double endToEndTime = endTime - data.CaptureStartTime;
                        performanceTracker.RecordTime($"EndToEnd_{data.Type}", endToEndTime);
                    }
                    
                    processedAny = true;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"Error publishing data in {threadName}: {ex.Message}");
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
            
            if (!processedAny)
            {
                // More efficient wait when queue is empty
                Thread.Yield();
            }
        }
        
        if (debugMode)
            UnityEngine.Debug.Log($"Stopped {threadName}");
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
        var msgSw = Stopwatch.StartNew();
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
        msgSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("Depth_MessageConstruction", msgSw.Elapsed.TotalMilliseconds);
        
        var publishSw = Stopwatch.StartNew();
        lcm.Publish(data.Settings.depthTopicName, imageMsg);
        publishSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("Depth_LCMPublish", publishSw.Elapsed.TotalMilliseconds);
        
        // Return byte array to pool after publishing
        ReturnByteArrayToPool(imageMsg.data);
    }
    
    private void PublishRGBToLCM(PublishData data)
    {
        var msgSw = Stopwatch.StartNew();
        var imageMsg = new sensor_msgs.Image();
        imageMsg.header = CreateHeader(data.Settings.sequenceNumber, data.Settings.rgbFrameID);
        imageMsg.height = data.Height;
        imageMsg.width = data.Width;
        imageMsg.encoding = rgbEncoding;
        imageMsg.is_bigendian = BitConverter.IsLittleEndian ? (byte)0 : (byte)1;
        imageMsg.step = data.Width * 3;
        imageMsg.data = data.Data;
        imageMsg.data_length = data.Data.Length;
        msgSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("RGB_MessageConstruction", msgSw.Elapsed.TotalMilliseconds);
        
        var publishSw = Stopwatch.StartNew();
        lcm.Publish(data.Settings.rgbTopicName, imageMsg);
        publishSw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("RGB_LCMPublish", publishSw.Elapsed.TotalMilliseconds);
    }
    
    private void PublishCameraInfo(CameraDepthSettings settings)
    {
        var sw = Stopwatch.StartNew();
        var cameraInfoMsg = CreateCameraInfoMessage(settings);
        
        var publishData = new PublishData
        {
            Type = PublishData.DataType.CameraInfo,
            CameraInfo = cameraInfoMsg,
            Settings = settings,
            CaptureStartTime = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds
        };
        publishQueue.Enqueue(publishData);
        
        sw.Stop();
        if (enablePerformanceTracking)
            performanceTracker.RecordTime("CameraInfo_Creation", sw.Elapsed.TotalMilliseconds);
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
    
    // Enhanced object pooling with better performance
    private byte[] GetPooledByteArray(int size)
    {
        if (byteArrayPool.TryDequeue(out byte[] array))
        {
            if (array.Length >= size)
                return array;
        }
        return new byte[size];
    }
    
    private float[] GetPooledFloatArray(int size)
    {
        if (floatArrayPool.TryDequeue(out float[] array))
        {
            if (array.Length >= size)
                return array;
        }
        return new float[size];
    }
    
    private void ReturnByteArrayToPool(byte[] array)
    {
        if (array == null) return;
        if (byteArrayPool.Count < 50) // Larger pool size
            byteArrayPool.Enqueue(array);
    }
    
    private void ReturnFloatArrayToPool(float[] array)
    {
        if (array == null) return;
        if (floatArrayPool.Count < 50) // Larger pool size
            floatArrayPool.Enqueue(array);
    }
    
    void OnDestroy()
    {
        isRunning = false;
        
        // Log final performance summary
        if (enablePerformanceTracking)
        {
            UnityEngine.Debug.Log("=== FINAL PERFORMANCE SUMMARY ===");
            UnityEngine.Debug.Log(performanceTracker.GetSummary());
        }
        
        // Wait for all publishing threads to finish
        if (publishThreads != null)
        {
            foreach (var thread in publishThreads)
            {
                if (thread != null)
                    thread.Join(1000);
            }
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
    
    // Public API methods (unchanged)
    public void AddCamera(Camera camera, string depthTopic = null, string rgbTopic = null, string cameraInfoTopic = null)
    {
        if (camera == null) return;
        
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
        
        if (newSettings.captureDepth)
        {
            camera.depthTextureMode = DepthTextureMode.Depth;
        }
        
        if (newSettings.captureRGB)
        {
            SetupCameraRenderTexture(newSettings);
        }
        
        cameraSettings.Add(newSettings);
        
        if (debugMode)
            UnityEngine.Debug.Log($"Added camera {camera.name} for capture");
    }
    
    public void RemoveCamera(Camera camera)
    {
        if (camera == null) return;
        
        for (int i = cameraSettings.Count - 1; i >= 0; i--)
        {
            if (cameraSettings[i].camera == camera)
            {
                var settings = cameraSettings[i];
                
                MultiCameraPersistentDepthFeature.CleanupCameraDepthTexture(settings.cameraInstanceID);
                
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
                    UnityEngine.Debug.Log($"Removed camera {camera.name} from capture");
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