using UnityEngine;
using UnityEngine.Rendering;
using LCM.LCM;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
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
    public bool useNativeResolution = true; // Use camera's actual render resolution
    
    [HideInInspector] public bool captureRequested = false;
    [HideInInspector] public float nextCaptureTime = 0f;
    [HideInInspector] public int cameraInstanceID;
    [HideInInspector] public uint sequenceNumber = 0;
}

public class MultiCameraLCMDepthCapture : MonoBehaviour
{
    [Header("Global Settings")]
    [SerializeField] private bool debugMode = true;
    
    [Header("Performance Settings")]
    [SerializeField] private bool useAsyncReadback = true;
    [SerializeField] private int maxConcurrentReadbacks = 4;
    [SerializeField] private bool useBackgroundThreads = true;
    [SerializeField] private int backgroundThreadCount = 2;
    
    [Header("LCM Settings")]
    [SerializeField] private string lcmURL = "udpm://239.255.76.67:7667";
    [SerializeField] private string depthEncoding = "32FC1"; // 32-bit float, 1 channel
    [SerializeField] private string rgbEncoding = "rgb8"; // 8-bit RGB, 3 channels
    
    [Header("Camera Configurations")]
    [SerializeField] private List<CameraDepthSettings> cameraSettings = new List<CameraDepthSettings>();
    
    [Header("Global Controls")]
    [SerializeField] private KeyCode captureAllKey = KeyCode.Space;
    [SerializeField] private bool globalContinuousMode = false;
    [SerializeField] private float globalPublishRate = 10f;
    
    private LCM.LCM.LCM lcm;
    private Dictionary<int, RenderTexture> cameraRenderTextures = new Dictionary<int, RenderTexture>();
    
    // Performance optimization fields
    private Dictionary<int, Texture2D> pooledDepthTextures = new Dictionary<int, Texture2D>();
    private Dictionary<int, Texture2D> pooledRGBTextures = new Dictionary<int, Texture2D>();
    private ConcurrentQueue<PublishTask> publishQueue = new ConcurrentQueue<PublishTask>();
    private int activeReadbacks = 0;
    private CancellationTokenSource cancellationTokenSource;
    private Task[] backgroundTasks;
    
    private struct PublishTask
    {
        public TaskType type;
        public byte[] data;
        public int width;
        public int height;
        public float[] depthData; // For depth tasks
        public CameraInfoData cameraInfoData; // For camera info tasks
        public PublishMetadata metadata; // Topic names, frame IDs, etc.
    }
    
    private struct PublishMetadata
    {
        public string topicName;
        public string frameId;
        public uint sequenceNumber;
        public string encoding;
    }
    
    private struct CameraInfoData
    {
        public int width;
        public int height;
        public float fx;
        public float fy;
        public float cx;
        public float cy;
        public uint sequenceNumber;
        public string frameId;
        public string topicName;
    }
    
    private enum TaskType
    {
        Depth,
        RGB,
        CameraInfo
    }
    
    void Start()
    {
        // Initialize cancellation token
        cancellationTokenSource = new CancellationTokenSource();
        
        // Auto-detect cameras if none are configured
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
            
            // Store camera instance ID for lookup
            settings.cameraInstanceID = settings.camera.GetInstanceID();
            
            // CRITICAL: Enable depth texture on each camera for URP
            if (settings.captureDepth)
            {
                settings.camera.depthTextureMode = DepthTextureMode.Depth;
            }
            
            // Setup render texture for RGB capture if needed
            if (settings.captureRGB)
            {
                SetupCameraRenderTexture(settings);
            }
            
            // Pre-allocate pooled textures for performance
            if (useAsyncReadback)
            {
                SetupPooledTextures(settings);
            }
            
            if (debugMode)
            {
                Debug.Log($"Camera configured: {settings.camera.name}");
                Debug.Log($"  Depth topic: {settings.depthTopicName}");
                Debug.Log($"  RGB topic: {settings.rgbTopicName}");
                Debug.Log($"  Camera info topic: {settings.cameraInfoTopicName}");
                Debug.Log($"  Near: {settings.camera.nearClipPlane}m, Far: {settings.camera.farClipPlane}m");
                
                if (settings.continuousMode || globalContinuousMode)
                {
                    float rate = globalContinuousMode ? globalPublishRate : settings.publishRate;
                    Debug.Log($"  Continuous mode: {rate} Hz");
                }
                else
                {
                    Debug.Log($"  Manual capture key: {settings.captureKey}");
                }
            }
        }
        
        try
        {
            // Initialize LCM
            lcm = new LCM.LCM.LCM(lcmURL);
            
            // Start background publishing threads if enabled
            if (useBackgroundThreads)
            {
                StartBackgroundThreads();
            }
            
            if (debugMode)
            {
                Debug.Log($"LCM initialized with URL: {lcmURL}");
                Debug.Log($"Press '{captureAllKey}' to capture all cameras");
                Debug.Log($"Configured {cameraSettings.Count} cameras for capture");
                Debug.Log($"Performance: AsyncReadback={useAsyncReadback}, BackgroundThreads={useBackgroundThreads}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error during LCM initialization: " + ex.Message);
            Debug.LogException(ex);
        }
    }
    
    void SetupCameraRenderTexture(CameraDepthSettings settings)
    {
        int width = settings.useNativeResolution ? settings.camera.pixelWidth : settings.rgbWidth;
        int height = settings.useNativeResolution ? settings.camera.pixelHeight : settings.rgbHeight;
        
        // Fallback to specified dimensions if camera dimensions are invalid
        if (width <= 0 || height <= 0)
        {
            width = settings.rgbWidth;
            height = settings.rgbHeight;
        }
        
        RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        rt.name = $"RGB_Capture_{settings.camera.name}";
        rt.Create();
        
        cameraRenderTextures[settings.cameraInstanceID] = rt;
        
        if (debugMode)
            Debug.Log($"Created RGB render texture for {settings.camera.name}: {width}x{height}");
    }
    
    void SetupPooledTextures(CameraDepthSettings settings)
    {
        // Setup pooled depth texture
        if (settings.captureDepth)
        {
            RTHandle depthTexture = MultiCameraPersistentDepthFeature.GetPersistentDepthTexture(settings.cameraInstanceID);
            if (depthTexture != null && depthTexture.rt != null)
            {
                Texture2D pooledDepth = new Texture2D(depthTexture.rt.width, depthTexture.rt.height, TextureFormat.RFloat, false);
                pooledDepthTextures[settings.cameraInstanceID] = pooledDepth;
            }
        }
        
        // Setup pooled RGB texture
        if (settings.captureRGB && cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture rt))
        {
            Texture2D pooledRGB = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            pooledRGBTextures[settings.cameraInstanceID] = pooledRGB;
        }
    }
    
    void StartBackgroundThreads()
    {
        backgroundTasks = new Task[backgroundThreadCount];
        for (int i = 0; i < backgroundThreadCount; i++)
        {
            backgroundTasks[i] = Task.Run(() => BackgroundPublishWorker(cancellationTokenSource.Token));
        }
        
        if (debugMode)
            Debug.Log($"Started {backgroundThreadCount} background publishing threads");
    }
    
    async void BackgroundPublishWorker(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (publishQueue.TryDequeue(out PublishTask task))
            {
                try
                {
                    switch (task.type)
                    {
                        case TaskType.Depth:
                            PublishDepthToLCMBackground(task.depthData, task.width, task.height, task.metadata);
                            break;
                        case TaskType.RGB:
                            PublishRGBToLCMBackground(task.data, task.width, task.height, task.metadata);
                            break;
                        case TaskType.CameraInfo:
                            PublishCameraInfoBackground(task.cameraInfoData);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Background publish error: {ex.Message}");
                }
            }
            else
            {
                // No work available, sleep briefly
                await Task.Delay(1, cancellationToken);
            }
        }
    }
    
    void Update()
    {
        if (lcm == null) return;
        
        // Check for global capture key
        if (Input.GetKeyDown(captureAllKey))
        {
            foreach (var settings in cameraSettings)
            {
                if (settings.enabled && settings.camera != null)
                {
                    settings.captureRequested = true;
                }
            }
            if (debugMode) Debug.Log("Capture requested for all cameras");
        }
        
        // Process each camera
        foreach (var settings in cameraSettings)
        {
            if (!settings.enabled || settings.camera == null) continue;
            
            bool shouldCapture = false;
            
            // Check continuous mode (global or per-camera)
            if (globalContinuousMode || settings.continuousMode)
            {
                if (Time.time >= settings.nextCaptureTime)
                {
                    float rate = globalContinuousMode ? globalPublishRate : settings.publishRate;
                    settings.nextCaptureTime = Time.time + (1f / rate);
                    shouldCapture = true;
                }
            }
            
            // Check individual camera key
            if (Input.GetKeyDown(settings.captureKey))
            {
                shouldCapture = true;
                if (debugMode) Debug.Log($"Capture requested for camera: {settings.camera.name}");
            }
            
            // Check if capture was already requested
            if (settings.captureRequested)
            {
                shouldCapture = true;
                settings.captureRequested = false;
            }
            
            if (shouldCapture)
            {
                // Skip if too many concurrent readbacks are active
                if (useAsyncReadback && activeReadbacks >= maxConcurrentReadbacks)
                {
                    if (debugMode) Debug.Log($"Skipping capture for {settings.camera.name} - too many active readbacks");
                    continue;
                }
                
                CaptureAndPublishAll(settings);
            }
        }
    }
    
    private void CaptureAndPublishAll(CameraDepthSettings settings)
    {
        // Increment sequence number
        settings.sequenceNumber++;
        
        // Capture and publish depth if enabled
        if (settings.captureDepth)
        {
            if (useAsyncReadback)
                CaptureDepthForCameraAsync(settings);
            else
                CaptureDepthForCamera(settings);
        }
        
        // Capture and publish RGB if enabled
        if (settings.captureRGB)
        {
            if (useAsyncReadback)
                CaptureRGBForCameraAsync(settings);
            else
                CaptureRGBForCamera(settings);
        }
        
        // Publish camera info if enabled (this is fast, so do it immediately)
        if (settings.publishCameraInfo)
        {
            if (useBackgroundThreads)
            {
                // Pre-calculate camera info data on main thread
                CameraInfoData cameraInfoData = CalculateCameraInfoData(settings);
                
                PublishTask task = new PublishTask
                {
                    type = TaskType.CameraInfo,
                    cameraInfoData = cameraInfoData
                };
                publishQueue.Enqueue(task);
            }
            else
            {
                PublishCameraInfo(settings);
            }
        }
    }
    
    private void CaptureDepthForCameraAsync(CameraDepthSettings settings)
    {
        // Get the persistent depth texture for this camera
        RTHandle depthTexture = MultiCameraPersistentDepthFeature.GetPersistentDepthTexture(settings.cameraInstanceID);
        
        if (depthTexture == null || depthTexture.rt == null)
        {
            if (debugMode)
            {
                Debug.LogWarning($"Persistent depth texture not available for camera: {settings.camera.name}");
            }
            return;
        }
        
        Texture sourceTexture = depthTexture.rt;
        
        if (sourceTexture.width <= 4 || sourceTexture.height <= 4)
        {
            if (debugMode)
            {
                Debug.LogWarning($"Invalid depth texture size for camera {settings.camera.name}: {sourceTexture.width}x{sourceTexture.height}");
            }
            return;
        }
        
        // Create temp RT for depth data
        RenderTexture tempRT = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.RFloat);
        Graphics.Blit(sourceTexture, tempRT);
        
        // Start async readback
        Interlocked.Increment(ref activeReadbacks);
        
        AsyncGPUReadback.Request(tempRT, 0, TextureFormat.RFloat, (AsyncGPUReadbackRequest request) =>
        {
            Interlocked.Decrement(ref activeReadbacks);
            RenderTexture.ReleaseTemporary(tempRT);
            
            if (request.hasError)
            {
                Debug.LogError($"Async GPU readback error for depth texture: {settings.camera.name}");
                return;
            }
            
            try
            {
                // Process depth data in background thread
                var rawData = request.GetData<float>();
                float[] depthArray = new float[rawData.Length];
                rawData.CopyTo(depthArray);
                
                // Linearize depth values
                float[] linearDepthValues = LinearizeDepthValues(depthArray, settings.camera);
                
                if (useBackgroundThreads)
                {
                    PublishTask task = new PublishTask
                    {
                        type = TaskType.Depth,
                        depthData = linearDepthValues,
                        width = sourceTexture.width,
                        height = sourceTexture.height,
                        metadata = new PublishMetadata
                        {
                            topicName = settings.depthTopicName,
                            frameId = settings.depthFrameID,
                            sequenceNumber = settings.sequenceNumber,
                            encoding = depthEncoding
                        }
                    };
                    publishQueue.Enqueue(task);
                }
                else
                {
                    PublishMetadata metadata = new PublishMetadata
                    {
                        topicName = settings.depthTopicName,
                        frameId = settings.depthFrameID,
                        sequenceNumber = settings.sequenceNumber,
                        encoding = depthEncoding
                    };
                    PublishDepthToLCMBackground(linearDepthValues, sourceTexture.width, sourceTexture.height, metadata);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error processing async depth readback: {ex.Message}");
            }
        });
    }
    
    private void CaptureRGBForCameraAsync(CameraDepthSettings settings)
    {
        if (!cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture renderTexture))
        {
            if (debugMode)
                Debug.LogWarning($"RGB render texture not found for camera: {settings.camera.name}");
            return;
        }
        
        // Store original target texture
        RenderTexture originalTarget = settings.camera.targetTexture;
        
        // Temporarily set camera to render to our capture texture
        settings.camera.targetTexture = renderTexture;
        settings.camera.Render();
        
        // Restore original target
        settings.camera.targetTexture = originalTarget;
        
        // Start async readback
        Interlocked.Increment(ref activeReadbacks);
        
        AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGB24, (AsyncGPUReadbackRequest request) =>
        {
            Interlocked.Decrement(ref activeReadbacks);
            
            if (request.hasError)
            {
                Debug.LogError($"Async GPU readback error for RGB texture: {settings.camera.name}");
                return;
            }
            
            try
            {
                // Get RGB data
                var rawData = request.GetData<byte>();
                byte[] rgbArray = new byte[rawData.Length];
                rawData.CopyTo(rgbArray);
                
                if (useBackgroundThreads)
                {
                    PublishTask task = new PublishTask
                    {
                        type = TaskType.RGB,
                        data = rgbArray,
                        width = renderTexture.width,
                        height = renderTexture.height,
                        metadata = new PublishMetadata
                        {
                            topicName = settings.rgbTopicName,
                            frameId = settings.rgbFrameID,
                            sequenceNumber = settings.sequenceNumber,
                            encoding = rgbEncoding
                        }
                    };
                    publishQueue.Enqueue(task);
                }
                else
                {
                    PublishMetadata metadata = new PublishMetadata
                    {
                        topicName = settings.rgbTopicName,
                        frameId = settings.rgbFrameID,
                        sequenceNumber = settings.sequenceNumber,
                        encoding = rgbEncoding
                    };
                    PublishRGBToLCMBackground(rgbArray, renderTexture.width, renderTexture.height, metadata);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error processing async RGB readback: {ex.Message}");
            }
        });
    }
    
    // Legacy synchronous methods (kept for fallback)
    private void CaptureDepthForCamera(CameraDepthSettings settings)
    {
        RTHandle depthTexture = MultiCameraPersistentDepthFeature.GetPersistentDepthTexture(settings.cameraInstanceID);
        
        if (depthTexture == null || depthTexture.rt == null)
        {
            if (debugMode)
                Debug.LogWarning($"Persistent depth texture not available for camera: {settings.camera.name}");
            return;
        }
        
        Texture sourceTexture = depthTexture.rt;
        if (sourceTexture.width <= 4 || sourceTexture.height <= 4) return;
        
        CaptureAndPublishDepth(sourceTexture, settings);
    }
    
    private void CaptureRGBForCamera(CameraDepthSettings settings)
    {
        if (!cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture renderTexture))
        {
            if (debugMode)
                Debug.LogWarning($"RGB render texture not found for camera: {settings.camera.name}");
            return;
        }
        
        RenderTexture originalTarget = settings.camera.targetTexture;
        settings.camera.targetTexture = renderTexture;
        settings.camera.Render();
        settings.camera.targetTexture = originalTarget;
        
        CaptureAndPublishRGB(renderTexture, settings);
    }
    
    private void CaptureAndPublishDepth(Texture sourceTexture, CameraDepthSettings settings)
    {
        int width = sourceTexture.width;
        int height = sourceTexture.height;
        
        RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.RFloat);
        Graphics.Blit(sourceTexture, tempRT);
        
        RenderTexture.active = tempRT;
        Texture2D depthImage = pooledDepthTextures.ContainsKey(settings.cameraInstanceID) 
            ? pooledDepthTextures[settings.cameraInstanceID] 
            : new Texture2D(width, height, TextureFormat.RFloat, false);
        
        depthImage.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        depthImage.Apply();
        RenderTexture.active = null;
        
        Color[] rawDepthPixels = depthImage.GetPixels();
        float[] linearDepthValues = LinearizeDepthValues(rawDepthPixels, settings.camera);
        
        PublishDepthToLCM(linearDepthValues, width, height, settings);
        
        RenderTexture.ReleaseTemporary(tempRT);
        if (!pooledDepthTextures.ContainsKey(settings.cameraInstanceID))
            DestroyImmediate(depthImage);
    }
    
    private void CaptureAndPublishRGB(RenderTexture sourceTexture, CameraDepthSettings settings)
    {
        int width = sourceTexture.width;
        int height = sourceTexture.height;
        
        RenderTexture.active = sourceTexture;
        Texture2D rgbImage = pooledRGBTextures.ContainsKey(settings.cameraInstanceID)
            ? pooledRGBTextures[settings.cameraInstanceID]
            : new Texture2D(width, height, TextureFormat.RGB24, false);
        
        rgbImage.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        rgbImage.Apply();
        RenderTexture.active = null;
        
        byte[] rgbData = rgbImage.GetRawTextureData();
        PublishRGBToLCM(rgbData, width, height, settings);
        
        if (!pooledRGBTextures.ContainsKey(settings.cameraInstanceID))
            DestroyImmediate(rgbImage);
    }
    
    private float[] LinearizeDepthValues(Color[] rawDepthPixels, Camera camera)
    {
        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        float[] linearDepths = new float[rawDepthPixels.Length];
        
        for (int i = 0; i < rawDepthPixels.Length; i++)
        {
            float rawDepth = rawDepthPixels[i].r;
            linearDepths[i] = CorrectPerspectiveLinearization(rawDepth, near, far);
        }
        
        return linearDepths;
    }
    
    private float[] LinearizeDepthValues(float[] rawDepthValues, Camera camera)
    {
        float near = camera.nearClipPlane;
        float far = camera.farClipPlane;
        float[] linearDepths = new float[rawDepthValues.Length];
        
        for (int i = 0; i < rawDepthValues.Length; i++)
        {
            linearDepths[i] = CorrectPerspectiveLinearization(rawDepthValues[i], near, far);
        }
        
        return linearDepths;
    }
    
    private float CorrectPerspectiveLinearization(float rawDepth, float near, float far)
    {
        float depth01;
        
        if (SystemInfo.usesReversedZBuffer)
        {
            depth01 = 1.0f - rawDepth;
        }
        else
        {
            depth01 = rawDepth;
        }
        
        if (depth01 <= 0.0001f) return near;
        if (depth01 >= 0.9999f) return far;
        
        float linearDepth = (near * far) / (far - depth01 * (far - near));
        return Mathf.Clamp(linearDepth, near, far * 2.0f);
    }
    
    private void PublishDepthToLCM(float[] depthValues, int width, int height, CameraDepthSettings settings)
    {
        if (useBackgroundThreads)
        {
            PublishTask task = new PublishTask
            {
                type = TaskType.Depth,
                depthData = depthValues,
                width = width,
                height = height,
                metadata = new PublishMetadata
                {
                    topicName = settings.depthTopicName,
                    frameId = settings.depthFrameID,
                    sequenceNumber = settings.sequenceNumber,
                    encoding = depthEncoding
                }
            };
            publishQueue.Enqueue(task);
        }
        else
        {
            PublishMetadata metadata = new PublishMetadata
            {
                topicName = settings.depthTopicName,
                frameId = settings.depthFrameID,
                sequenceNumber = settings.sequenceNumber,
                encoding = depthEncoding
            };
            PublishDepthToLCMBackground(depthValues, width, height, metadata);
        }
    }
    
    private void PublishRGBToLCM(byte[] rgbData, int width, int height, CameraDepthSettings settings)
    {
        if (useBackgroundThreads)
        {
            PublishTask task = new PublishTask
            {
                type = TaskType.RGB,
                data = rgbData,
                width = width,
                height = height,
                metadata = new PublishMetadata
                {
                    topicName = settings.rgbTopicName,
                    frameId = settings.rgbFrameID,
                    sequenceNumber = settings.sequenceNumber,
                    encoding = rgbEncoding
                }
            };
            publishQueue.Enqueue(task);
        }
        else
        {
            PublishMetadata metadata = new PublishMetadata
            {
                topicName = settings.rgbTopicName,
                frameId = settings.rgbFrameID,
                sequenceNumber = settings.sequenceNumber,
                encoding = rgbEncoding
            };
            PublishRGBToLCMBackground(rgbData, width, height, metadata);
        }
    }
    
    private void PublishDepthToLCMBackground(float[] depthValues, int width, int height, PublishMetadata metadata)
    {
        try
        {
            sensor_msgs.Image imageMsg = new sensor_msgs.Image();
            imageMsg.header = CreateHeaderBackground(metadata.sequenceNumber, metadata.frameId);
            imageMsg.height = height;
            imageMsg.width = width;
            imageMsg.encoding = metadata.encoding;
            imageMsg.is_bigendian = BitConverter.IsLittleEndian ? (byte)0 : (byte)1;
            imageMsg.step = width * sizeof(float);
            
            int dataSize = depthValues.Length * sizeof(float);
            imageMsg.data = new byte[dataSize];
            imageMsg.data_length = dataSize;
            
            Buffer.BlockCopy(depthValues, 0, imageMsg.data, 0, dataSize);
            
            lcm.Publish(metadata.topicName, imageMsg);
            
            if (debugMode)
                Debug.Log($"Published depth image ({width}x{height}) to topic: {metadata.topicName}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error publishing depth: {ex.Message}");
        }
    }
    
    private void PublishRGBToLCMBackground(byte[] rgbData, int width, int height, PublishMetadata metadata)
    {
        try
        {
            sensor_msgs.Image imageMsg = new sensor_msgs.Image();
            imageMsg.header = CreateHeaderBackground(metadata.sequenceNumber, metadata.frameId);
            imageMsg.height = height;
            imageMsg.width = width;
            imageMsg.encoding = metadata.encoding;
            imageMsg.is_bigendian = BitConverter.IsLittleEndian ? (byte)0 : (byte)1;
            imageMsg.step = width * 3;
            
            imageMsg.data = rgbData;
            imageMsg.data_length = rgbData.Length;
            
            lcm.Publish(metadata.topicName, imageMsg);
            
            if (debugMode)
                Debug.Log($"Published RGB image ({width}x{height}) to topic: {metadata.topicName}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error publishing RGB: {ex.Message}");
        }
    }
    
    private CameraInfoData CalculateCameraInfoData(CameraDepthSettings settings)
    {
        // Get image dimensions (use RGB dimensions if RGB is enabled, otherwise use camera resolution)
        int width, height;
        if (settings.captureRGB && cameraRenderTextures.TryGetValue(settings.cameraInstanceID, out RenderTexture rt))
        {
            width = rt.width;
            height = rt.height;
        }
        else
        {
            width = settings.camera.pixelWidth > 0 ? settings.camera.pixelWidth : settings.rgbWidth;
            height = settings.camera.pixelHeight > 0 ? settings.camera.pixelHeight : settings.rgbHeight;
        }
        
        // Calculate intrinsic matrix from Unity camera parameters
        float fovY = settings.camera.fieldOfView * Mathf.Deg2Rad;
        float fovX = 2.0f * Mathf.Atan(Mathf.Tan(fovY * 0.5f) * settings.camera.aspect);
        float fy = height / (2.0f * Mathf.Tan(fovY * 0.5f));
        float fx = width / (2.0f * Mathf.Tan(fovX * 0.5f));
        float cx = width * 0.5f;
        float cy = height * 0.5f;
        
        return new CameraInfoData
        {
            width = width,
            height = height,
            fx = fx,
            fy = fy,
            cx = cx,
            cy = cy,
            sequenceNumber = settings.sequenceNumber,
            frameId = settings.cameraInfoFrameID,
            topicName = settings.cameraInfoTopicName
        };
    }
    
    private void PublishCameraInfo(CameraDepthSettings settings)
    {
        if (useBackgroundThreads)
        {
            // Pre-calculate camera info data on main thread
            CameraInfoData cameraInfoData = CalculateCameraInfoData(settings);
            
            PublishTask task = new PublishTask
            {
                type = TaskType.CameraInfo,
                cameraInfoData = cameraInfoData
            };
            publishQueue.Enqueue(task);
        }
        else
        {
            PublishCameraInfoBackground(CalculateCameraInfoData(settings));
        }
    }
    
    private void PublishCameraInfoBackground(CameraInfoData cameraInfoData)
    {
        try
        {
            sensor_msgs.CameraInfo cameraInfoMsg = new sensor_msgs.CameraInfo();
            cameraInfoMsg.header = CreateHeaderBackground(cameraInfoData.sequenceNumber, cameraInfoData.frameId);
            
            cameraInfoMsg.height = cameraInfoData.height;
            cameraInfoMsg.width = cameraInfoData.width;
            cameraInfoMsg.distortion_model = "plumb_bob";
            cameraInfoMsg.D_length = 5;
            cameraInfoMsg.D = new double[5] { 0.0, 0.0, 0.0, 0.0, 0.0 };
            
            cameraInfoMsg.K[0] = cameraInfoData.fx; cameraInfoMsg.K[1] = 0;  cameraInfoMsg.K[2] = cameraInfoData.cx;
            cameraInfoMsg.K[3] = 0;  cameraInfoMsg.K[4] = cameraInfoData.fy; cameraInfoMsg.K[5] = cameraInfoData.cy;
            cameraInfoMsg.K[6] = 0;  cameraInfoMsg.K[7] = 0;  cameraInfoMsg.K[8] = 1;
            
            cameraInfoMsg.R[0] = 1; cameraInfoMsg.R[1] = 0; cameraInfoMsg.R[2] = 0;
            cameraInfoMsg.R[3] = 0; cameraInfoMsg.R[4] = 1; cameraInfoMsg.R[5] = 0;
            cameraInfoMsg.R[6] = 0; cameraInfoMsg.R[7] = 0; cameraInfoMsg.R[8] = 1;
            
            cameraInfoMsg.P[0] = cameraInfoData.fx; cameraInfoMsg.P[1] = 0;  cameraInfoMsg.P[2] = cameraInfoData.cx; cameraInfoMsg.P[3] = 0;
            cameraInfoMsg.P[4] = 0;  cameraInfoMsg.P[5] = cameraInfoData.fy; cameraInfoMsg.P[6] = cameraInfoData.cy; cameraInfoMsg.P[7] = 0;
            cameraInfoMsg.P[8] = 0;  cameraInfoMsg.P[9] = 0;  cameraInfoMsg.P[10] = 1; cameraInfoMsg.P[11] = 0;
            
            cameraInfoMsg.binning_x = 0;
            cameraInfoMsg.binning_y = 0;
            
            cameraInfoMsg.roi = new sensor_msgs.RegionOfInterest();
            cameraInfoMsg.roi.x_offset = 0;
            cameraInfoMsg.roi.y_offset = 0;
            cameraInfoMsg.roi.height = (int)cameraInfoData.height;
            cameraInfoMsg.roi.width = (int)cameraInfoData.width;
            cameraInfoMsg.roi.do_rectify = false;
            
            lcm.Publish(cameraInfoData.topicName, cameraInfoMsg);
            
            if (debugMode)
                Debug.Log($"Published camera info to topic: {cameraInfoData.topicName}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error publishing camera info: {ex.Message}");
        }
    }
    
    private std_msgs.Header CreateHeaderBackground(uint sequenceNumber, string frameId)
    {
        std_msgs.Header header = new std_msgs.Header();
        header.seq = (int)sequenceNumber;
        header.stamp = new std_msgs.Time();
        header.stamp.sec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        header.stamp.nsec = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000) * 1000000);
        header.frame_id = frameId;
        return header;
    }
    
    private std_msgs.Header CreateHeader(uint sequenceNumber, string frameId)
    {
        std_msgs.Header header = new std_msgs.Header();
        header.seq = (int)sequenceNumber;
        header.stamp = new std_msgs.Time();
        header.stamp.sec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        header.stamp.nsec = (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000) * 1000000);
        header.frame_id = frameId;
        return header;
    }
    
    void OnDisable()
    {
        // Cancel background threads
        if (cancellationTokenSource != null)
        {
            cancellationTokenSource.Cancel();
            
            if (backgroundTasks != null)
            {
                Task.WaitAll(backgroundTasks, 1000); // Wait up to 1 second
            }
        }
        
        // Clean up
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
        
        foreach (var kvp in pooledDepthTextures)
        {
            if (kvp.Value != null)
                DestroyImmediate(kvp.Value);
        }
        pooledDepthTextures.Clear();
        
        foreach (var kvp in pooledRGBTextures)
        {
            if (kvp.Value != null)
                DestroyImmediate(kvp.Value);
        }
        pooledRGBTextures.Clear();
    }
    
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
        
        camera.depthTextureMode = DepthTextureMode.Depth;
        SetupCameraRenderTexture(newSettings);
        
        if (useAsyncReadback)
            SetupPooledTextures(newSettings);
        
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
                
                if (pooledDepthTextures.TryGetValue(settings.cameraInstanceID, out Texture2D depthTex))
                {
                    if (depthTex != null) DestroyImmediate(depthTex);
                    pooledDepthTextures.Remove(settings.cameraInstanceID);
                }
                
                if (pooledRGBTextures.TryGetValue(settings.cameraInstanceID, out Texture2D rgbTex))
                {
                    if (rgbTex != null) DestroyImmediate(rgbTex);
                    pooledRGBTextures.Remove(settings.cameraInstanceID);
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