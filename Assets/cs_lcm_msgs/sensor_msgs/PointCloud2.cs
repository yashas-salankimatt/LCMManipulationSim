/* LCM type definition class file
 * This file was automatically generated by lcm-gen 1.5.1
 * DO NOT MODIFY BY HAND!!!!
 */

using System;
using System.Collections.Generic;
using System.IO;
using LCM.LCM;
 
namespace sensor_msgs
{
    public sealed class PointCloud2 : LCM.LCM.LCMEncodable
    {
        public int fields_length;
        public int data_length;
        public std_msgs.Header header;
        public int height;
        public int width;
        public sensor_msgs.PointField[] fields;
        public bool is_bigendian;
        public int point_step;
        public int row_step;
        public byte[] data;
        public bool is_dense;
 
        public PointCloud2()
        {
        }
 
        public static readonly ulong LCM_FINGERPRINT;
        public static readonly ulong LCM_FINGERPRINT_BASE = 0xeabe7183c4d74215L;
 
        static PointCloud2()
        {
            LCM_FINGERPRINT = _hashRecursive(new List<String>());
        }
 
        public static ulong _hashRecursive(List<String> classes)
        {
            if (classes.Contains("sensor_msgs.PointCloud2"))
                return 0L;
 
            classes.Add("sensor_msgs.PointCloud2");
            ulong hash = LCM_FINGERPRINT_BASE
                 + std_msgs.Header._hashRecursive(classes)
                 + sensor_msgs.PointField._hashRecursive(classes)
                ;
            classes.RemoveAt(classes.Count - 1);
            return (hash<<1) + ((hash>>63)&1);
        }
 
        public void Encode(LCMDataOutputStream outs)
        {
            outs.Write((long) LCM_FINGERPRINT);
            _encodeRecursive(outs);
        }
 
        public void _encodeRecursive(LCMDataOutputStream outs)
        {
            outs.Write(this.fields_length); 
 
            outs.Write(this.data_length); 
 
            this.header._encodeRecursive(outs); 
 
            outs.Write(this.height); 
 
            outs.Write(this.width); 
 
            for (int a = 0; a < this.fields_length; a++) {
                this.fields[a]._encodeRecursive(outs); 
            }
 
            outs.Write(this.is_bigendian); 
 
            outs.Write(this.point_step); 
 
            outs.Write(this.row_step); 
 
            for (int a = 0; a < this.data_length; a++) {
                outs.Write(this.data[a]); 
            }
 
            outs.Write(this.is_dense); 
 
        }
 
        public PointCloud2(byte[] data) : this(new LCMDataInputStream(data))
        {
        }
 
        public PointCloud2(LCMDataInputStream ins)
        {
            if ((ulong) ins.ReadInt64() != LCM_FINGERPRINT)
                throw new System.IO.IOException("LCM Decode error: bad fingerprint");
 
            _decodeRecursive(ins);
        }
 
        public static sensor_msgs.PointCloud2 _decodeRecursiveFactory(LCMDataInputStream ins)
        {
            sensor_msgs.PointCloud2 o = new sensor_msgs.PointCloud2();
            o._decodeRecursive(ins);
            return o;
        }
 
        public void _decodeRecursive(LCMDataInputStream ins)
        {
            this.fields_length = ins.ReadInt32();
 
            this.data_length = ins.ReadInt32();
 
            this.header = std_msgs.Header._decodeRecursiveFactory(ins);
 
            this.height = ins.ReadInt32();
 
            this.width = ins.ReadInt32();
 
            this.fields = new sensor_msgs.PointField[(int) fields_length];
            for (int a = 0; a < this.fields_length; a++) {
                this.fields[a] = sensor_msgs.PointField._decodeRecursiveFactory(ins);
            }
 
            this.is_bigendian = ins.ReadBoolean();
 
            this.point_step = ins.ReadInt32();
 
            this.row_step = ins.ReadInt32();
 
            this.data = new byte[(int) data_length];
            for (int a = 0; a < this.data_length; a++) {
                this.data[a] = ins.ReadByte();
            }
 
            this.is_dense = ins.ReadBoolean();
 
        }
 
        public sensor_msgs.PointCloud2 Copy()
        {
            sensor_msgs.PointCloud2 outobj = new sensor_msgs.PointCloud2();
            outobj.fields_length = this.fields_length;
 
            outobj.data_length = this.data_length;
 
            outobj.header = this.header.Copy();
 
            outobj.height = this.height;
 
            outobj.width = this.width;
 
            outobj.fields = new sensor_msgs.PointField[(int) fields_length];
            for (int a = 0; a < this.fields_length; a++) {
                outobj.fields[a] = this.fields[a].Copy();
            }
 
            outobj.is_bigendian = this.is_bigendian;
 
            outobj.point_step = this.point_step;
 
            outobj.row_step = this.row_step;
 
            outobj.data = new byte[(int) data_length];
            for (int a = 0; a < this.data_length; a++) {
                outobj.data[a] = this.data[a];
            }
 
            outobj.is_dense = this.is_dense;
 
            return outobj;
        }
    }
}

