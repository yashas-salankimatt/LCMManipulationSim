using UnityEngine;
using UnityEngine.Rendering;
using System.IO;

public class LinearizedDepthCapture : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private string saveDirectory = "DepthCaptures";
    [SerializeField] private bool debugMode = true;
    
    [Header("Unity 6 URP Linearization")]
    [SerializeField] private int linearizationMethod = 4; // 1=Unity's Linear01Depth, 2=Simple Lerp, 3=Manual Formula, 4=Correct Perspective
    
    [Header("Output Options")]
    [SerializeField] private bool saveLinearEXR = true; // Real depth values in meters
    [SerializeField] private bool saveVisualizationPNG = true; // Normalized for viewing
    
    public Camera targetCamera;
    private bool captureRequested = false;
    
    void Start()
    {
        // Get the main camera
        // targetCamera = Camera.main ?? FindObjectOfType<Camera>();
        
        if (targetCamera == null)
        {
            Debug.LogError("No camera found!");
            return;
        }
        
        // CRITICAL: Enable depth texture on the camera for URP
        targetCamera.depthTextureMode = DepthTextureMode.Depth;
        
        // Create save directory
        string fullPath = Path.Combine(Application.dataPath, saveDirectory);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
        
        if (debugMode)
        {
            Debug.Log($"Depth capture ready. Camera: {targetCamera.name}");
            Debug.Log($"Camera near: {targetCamera.nearClipPlane}m, far: {targetCamera.farClipPlane}m");
            Debug.Log("Press 'C' key to capture depth texture");
        }
    }
    
    void Update()
    {
        // Capture depth when 'C' key is pressed
        if (Input.GetKeyDown(KeyCode.C))
        {
            captureRequested = true;
            if (debugMode) Debug.Log("Depth capture requested...");
        }
    }
    
    void OnRenderObject()
    {
        // This is the key: In Unity 6.0 URP, depth texture is only available here
        if (!captureRequested) return;
        
        captureRequested = false;
        
        // Get the global depth texture
        Texture depthTexture = Shader.GetGlobalTexture("_CameraDepthTexture");
        
        if (depthTexture == null || depthTexture.width <= 4 || depthTexture.height <= 4)
        {
            if (debugMode)
            {
                Debug.LogWarning($"Depth texture not available! Size: {depthTexture?.width}x{depthTexture?.height}");
                Debug.LogWarning("Make sure 'Depth Texture' is enabled in your URP Asset!");
            }
            return;
        }
        
        SaveDepthTexture(depthTexture);
    }
    
    private void SaveDepthTexture(Texture sourceTexture)
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
        
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        
        // Save linearized depth as EXR (real world units)
        if (saveLinearEXR)
        {
            string exrPath = Path.Combine(Application.dataPath, saveDirectory, $"LinearDepth_{timestamp}.exr");
            SaveLinearDepthEXR(linearDepthValues, width, height, exrPath);
        }
        
        // Save visualization PNG (normalized grayscale)
        if (saveVisualizationPNG)
        {
            Texture2D visibleDepth = ConvertDepthToGrayscale(rawDepthPixels, width, height);
            string pngPath = Path.Combine(Application.dataPath, saveDirectory, $"DepthVisualization_{timestamp}.png");
            
            byte[] pngData = visibleDepth.EncodeToPNG();
            File.WriteAllBytes(pngPath, pngData);
            
            if (debugMode)
                Debug.Log($"Depth visualization saved: {pngPath}");
                
            DestroyImmediate(visibleDepth);
        }
        
        // Cleanup
        RenderTexture.ReleaseTemporary(tempRT);
        DestroyImmediate(depthImage);
        
        #if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
        #endif
    }
    
    private float[] LinearizeDepthValues(Color[] rawDepthPixels)
    {
        float near = targetCamera.nearClipPlane;
        float far = targetCamera.farClipPlane;
        Vector4 zBufferParams = CalculateZBufferParams(near, far);
        
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
            float linearDepth = ConvertRawDepthToLinear(rawDepth, near, far);
            
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
            string[] methodNames = { "", "Unity Linear01Depth", "Simple Lerp (WRONG)", "Manual Formula", "Correct Perspective" };
            Debug.Log($"=== Unity 6 URP Depth Analysis ===");
            Debug.Log($"Linearization method: {linearizationMethod} ({methodNames[linearizationMethod]})");
            Debug.Log($"Raw depth range: {minRaw:F6} to {maxRaw:F6}");
            Debug.Log($"Linearized depth range: {minLinear:F3}m to {maxLinear:F3}m");
            Debug.Log($"Valid pixels: {validPixels}/{rawDepthPixels.Length}");
            Debug.Log($"Camera near: {near:F3}m, far: {far:F3}m");
            Debug.Log($"Platform info: Reversed Z = {SystemInfo.usesReversedZBuffer}");
            
            // Show conversion examples for debugging
            if (linearizationMethod == 4 && SystemInfo.usesReversedZBuffer)
            {
                Debug.Log($"Perspective conversion examples (Reversed Z):");
                float depth01_min = 1.0f - minRaw;
                float depth01_max = 1.0f - maxRaw;
                float linear_min = (near * far) / (far - depth01_min * (far - near));
                float linear_max = (near * far) / (far - depth01_max * (far - near));
                Debug.Log($"  Raw {minRaw:F6} → Depth01 {depth01_min:F6} → Linear {linear_min:F3}m");
                Debug.Log($"  Raw {maxRaw:F6} → Depth01 {depth01_max:F6} → Linear {linear_max:F3}m");
            }
            
            // Expected range check
            if (linearizationMethod == 4)
            {
                if (minLinear >= 0.3f && maxLinear <= 100.0f)
                {
                    Debug.Log($"✅ Depth range looks realistic for close objects!");
                }
                else if (minLinear > 100.0f)
                {
                    Debug.LogWarning($"⚠️ Minimum depth {minLinear:F1}m seems too far. Expected objects closer than 100m.");
                }
                
                // Provide scene-specific feedback
                Debug.Log($"💡 For a capsule at 1m and plane at 20m, expect range ~1-20m");
            }
            
            // General diagnostics
            if (maxLinear - minLinear < 0.1f && linearizationMethod != 4)
            {
                Debug.LogWarning("⚠️ Try Method 4 (Correct Perspective) for proper depth linearization");
            }
            else if (validPixels > 0 && linearizationMethod == 4)
            {
                Debug.Log($"✅ Perspective linearization complete! Range: {maxLinear - minLinear:F1}m");
            }
        }
        
        return linearDepths;
    }
    
    private float ConvertRawDepthToLinear(float rawDepth, float near, float far)
    {
        // Unity 6 URP provides multiple ways to linearize depth
        // Test different methods if one doesn't work properly
        
        switch (linearizationMethod)
        {
            case 1: // Unity's Linear01Depth function (currently has overflow issues)
                Vector4 zbufferParams = CalculateZBufferParams(near, far);
                float linear01 = Linear01Depth(rawDepth, zbufferParams);
                return linear01 * far;
                
            case 2: // Simple Lerp (WRONG - depth is not linear!)
                if (SystemInfo.usesReversedZBuffer)
                {
                    float invertedDepth = 1.0f - rawDepth;
                    return Mathf.Lerp(near, far, invertedDepth);
                }
                else
                {
                    return Mathf.Lerp(near, far, rawDepth);
                }
                
            case 3: // Manual formula (traditional method)
                return ManualDepthLinearization(rawDepth, near, far);
                
            case 4: // Correct Perspective Linearization (RECOMMENDED)
                return CorrectPerspectiveLinearization(rawDepth, near, far);
                
            default:
                return CorrectPerspectiveLinearization(rawDepth, near, far);
        }
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
    
    private float ManualDepthLinearization(float rawDepth, float near, float far)
    {
        // Traditional manual linearization (may not work in Unity 6 URP)
        float z;
        
        if (SystemInfo.usesReversedZBuffer)
        {
            // DirectX/Metal: 1.0 at near plane, 0.0 at far plane
            z = 1.0f - rawDepth;
        }
        else
        {
            // OpenGL: 0.0 at near plane, 1.0 at far plane
            z = rawDepth;
        }
        
        // Handle edge cases
        if (z <= 0.0001f) return far;   // Far plane
        if (z >= 0.9999f) return near; // Near plane
        
        // Convert to linear world-space distance
        return (2.0f * near * far) / (far + near - z * (far - near));
    }
    
    // Unity's built-in Linear01Depth function (handles all platform differences)
    private float Linear01Depth(float z, Vector4 zbufferParams)
    {
        // Unity's implementation that automatically handles:
        // - Reversed Z buffer (DirectX/Metal vs OpenGL)
        // - Platform-specific depth encoding
        // - URP depth texture format changes
        return 1.0f / (zbufferParams.z * z + zbufferParams.w);
    }
    
    // Calculate Unity's _ZBufferParams for the linearization
    private Vector4 CalculateZBufferParams(float near, float far)
    {
        // Unity's standard _ZBufferParams calculation
        // Format: (f-n)/n, 1, (f-n)/(n*f), 1/f
        float fpn = far / near;
        return new Vector4(
            1.0f - fpn,     // (f-n)/n = f/n - 1
            fpn,            // f/n  
            (1.0f - fpn) / far, // (f-n)/(n*f)
            1.0f / far      // 1/f
        );
    }
    
    private void SaveLinearDepthEXR(float[] linearDepths, int width, int height, string filePath)
    {
        // Create texture with linearized depth values
        Texture2D exrTexture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
        
        Color[] exrPixels = new Color[linearDepths.Length];
        
        for (int i = 0; i < linearDepths.Length; i++)
        {
            float depth = linearDepths[i];
            
            // Store depth in meters in red channel
            // Use -1 for invalid pixels (far plane, etc.)
            if (float.IsInfinity(depth) || depth <= 0)
            {
                depth = -1.0f;
            }
            
            // R = depth in meters, G = depth in meters, B = 0, A = validity flag
            float validity = (depth > 0) ? 1.0f : 0.0f;
            exrPixels[i] = new Color(depth, depth, 0.0f, validity);
        }
        
        exrTexture.SetPixels(exrPixels);
        exrTexture.Apply();
        
        // Save as EXR
        byte[] exrData = exrTexture.EncodeToEXR(Texture2D.EXRFlags.CompressZIP);
        File.WriteAllBytes(filePath, exrData);
        
        DestroyImmediate(exrTexture);
        
        if (debugMode)
        {
            Debug.Log($"Linear depth EXR saved: {filePath}");
            Debug.Log($"Format: Red/Green = depth in meters, Blue = 0, Alpha = validity");
            Debug.Log($"Invalid pixels (far plane) = -1.0 meters");
        }
    }
    
    private Texture2D ConvertDepthToGrayscale(Color[] depthPixels, int width, int height)
    {
        Color[] grayscalePixels = new Color[depthPixels.Length];
        
        // Find min/max depth values for better contrast (same as original)
        float minDepth = float.MaxValue;
        float maxDepth = float.MinValue;
        
        foreach (Color pixel in depthPixels)
        {
            float depth = pixel.r;
            minDepth = Mathf.Min(minDepth, depth);
            maxDepth = Mathf.Max(maxDepth, depth);
        }
        
        float depthRange = maxDepth - minDepth;
        if (depthRange < 0.001f) depthRange = 1.0f; // Avoid division by zero
        
        // Convert depth values to grayscale with contrast enhancement (same as original)
        for (int i = 0; i < depthPixels.Length; i++)
        {
            float depth = depthPixels[i].r;
            
            // Normalize depth to 0-1 range
            float normalizedDepth = (depth - minDepth) / depthRange;
            
            // Reverse depth so closer objects are brighter (optional)
            normalizedDepth = 1.0f - normalizedDepth;
            
            grayscalePixels[i] = new Color(normalizedDepth, normalizedDepth, normalizedDepth, 1.0f);
        }
        
        Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
        result.SetPixels(grayscalePixels);
        result.Apply();
        
        return result;
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