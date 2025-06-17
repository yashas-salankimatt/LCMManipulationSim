#!/usr/bin/env python3
"""
Recursively convert all STL files to binary format and backup originals.
Requires: pip install numpy-stl
"""

import os
import shutil
import struct
from pathlib import Path
from stl import mesh
import sys
import numpy as np


def is_ascii_stl(file_path):
    """Check if STL file is in ASCII format."""
    try:
        with open(file_path, 'rb') as f:
            # Read first 80 bytes
            header = f.read(80)
            # ASCII STL files typically start with "solid"
            if header.startswith(b'solid'):
                # Check if it's actually ASCII by looking for text patterns
                f.seek(0)
                try:
                    first_line = f.readline().decode('utf-8', errors='ignore').strip()
                    # Additional check: try to read as binary first
                    f.seek(0)
                    # Binary STL has 80-byte header + 4-byte triangle count
                    f.read(80)  # header
                    triangle_count_bytes = f.read(4)
                    if len(triangle_count_bytes) == 4:
                        triangle_count = struct.unpack('<I', triangle_count_bytes)[0]
                        # Check if file size matches binary format
                        file_size = os.path.getsize(file_path)
                        expected_size = 80 + 4 + (triangle_count * 50)
                        if file_size == expected_size:
                            return False  # It's binary
                    
                    return first_line.startswith('solid')
                except:
                    return True  # Assume ASCII if we can't determine
    except:
        pass
    return False


def convert_stl_to_binary(input_path, output_path):
    """Convert STL file to binary format."""
    try:
        # Load the mesh
        stl_mesh = mesh.Mesh.from_file(str(input_path))
        
        # Save as binary STL - try different methods based on library version
        try:
            # Method 1: Try with mode parameter as string
            stl_mesh.save(str(output_path), mode='binary')
        except (TypeError, AttributeError):
            try:
                # Method 2: Try with update_normals and binary format
                stl_mesh.update_normals()
                with open(str(output_path), 'wb') as f:
                    # Write 80-byte header
                    header = b'Binary STL created by conversion script' + b'\0' * (80 - 42)
                    f.write(header)
                    
                    # Write number of triangles
                    f.write(struct.pack('<I', len(stl_mesh.vectors)))
                    
                    # Write triangles
                    for i, triangle in enumerate(stl_mesh.vectors):
                        # Normal vector
                        normal = stl_mesh.normals[i]
                        f.write(struct.pack('<fff', normal[0], normal[1], normal[2]))
                        
                        # Vertices
                        for vertex in triangle:
                            f.write(struct.pack('<fff', vertex[0], vertex[1], vertex[2]))
                        
                        # Attribute byte count (usually 0)
                        f.write(struct.pack('<H', 0))
            except Exception as e2:
                # Method 3: Default save (should be binary by default in newer versions)
                stl_mesh.save(str(output_path))
        
        return True
    except Exception as e:
        print(f"Error converting {input_path}: {e}")
        return False


def create_backup(original_path):
    """Create backup by renaming original file without .stl extension."""
    backup_path = original_path.with_suffix('')
    
    # If backup already exists, add a number
    counter = 1
    while backup_path.exists():
        backup_path = original_path.with_suffix(f'.backup{counter}')
        counter += 1
    
    try:
        shutil.move(str(original_path), str(backup_path))
        return backup_path
    except Exception as e:
        print(f"Error creating backup for {original_path}: {e}")
        return None


def verify_binary_stl(file_path):
    """Verify that the STL file is in binary format."""
    try:
        with open(file_path, 'rb') as f:
            # Read header and triangle count
            header = f.read(80)
            triangle_count_bytes = f.read(4)
            
            if len(triangle_count_bytes) != 4:
                return False
                
            triangle_count = struct.unpack('<I', triangle_count_bytes)[0]
            
            # Check if file size matches binary format
            file_size = os.path.getsize(file_path)
            expected_size = 80 + 4 + (triangle_count * 50)
            
            return file_size == expected_size
    except:
        return False


def main():
    """Main function to process all STL files."""
    current_dir = Path.cwd()
    
    # Find all STL files recursively
    stl_files = list(current_dir.rglob('*.stl')) + list(current_dir.rglob('*.STL'))
    
    if not stl_files:
        print("No STL files found in current directory and subdirectories.")
        return
    
    print(f"Found {len(stl_files)} STL file(s)")
    
    converted_count = 0
    skipped_count = 0
    error_count = 0
    
    for stl_file in stl_files:
        print(f"\nProcessing: {stl_file.relative_to(current_dir)}")
        
        # Check if file is ASCII STL
        if not is_ascii_stl(stl_file):
            print("  → Already binary or not a valid STL, skipping")
            skipped_count += 1
            continue
        
        print("  → Detected ASCII STL, converting to binary...")
        
        # Create temporary file for conversion
        temp_file = stl_file.with_suffix('.stl.tmp')
        
        # Convert to binary
        if convert_stl_to_binary(stl_file, temp_file):
            # Verify the conversion worked
            if verify_binary_stl(temp_file):
                # Create backup of original
                backup_path = create_backup(stl_file)
                
                if backup_path:
                    # Move converted file to original location
                    try:
                        shutil.move(str(temp_file), str(stl_file))
                        print(f"  ✓ Converted to binary STL")
                        print(f"  ✓ Original backed up as: {backup_path.name}")
                        converted_count += 1
                    except Exception as e:
                        print(f"  ✗ Error replacing original: {e}")
                        # Restore original
                        if backup_path.exists():
                            shutil.move(str(backup_path), str(stl_file))
                        error_count += 1
                else:
                    print("  ✗ Failed to create backup")
                    error_count += 1
            else:
                print("  ✗ Conversion verification failed")
                error_count += 1
        else:
            print("  ✗ Conversion failed")
            error_count += 1
        
        # Clean up temp file if it exists
        if temp_file.exists():
            temp_file.unlink()
    
    # Summary
    print(f"\n{'='*50}")
    print(f"SUMMARY:")
    print(f"Converted: {converted_count}")
    print(f"Skipped (already binary): {skipped_count}")
    print(f"Errors: {error_count}")
    print(f"Total processed: {len(stl_files)}")
    
    if error_count > 0:
        print(f"\nNote: {error_count} files failed to convert.")
        print("This might be due to corrupted files or unsupported STL variants.")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nOperation cancelled by user.")
        sys.exit(1)
    except Exception as e:
        print(f"Unexpected error: {e}")
        sys.exit(1)