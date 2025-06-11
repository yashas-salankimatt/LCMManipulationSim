import cv2
depth_img = cv2.imread('LinearDepth_20241203_143022.exr', cv2.IMREAD_UNCHANGED)
depth_meters = depth_img[:, :, 0]  # Red channel = actual depth in meters

# Example: pixel at center of image
center_depth = depth_meters[height//2, width//2]
print(f"Center object is {center_depth:.2f} meters away")
