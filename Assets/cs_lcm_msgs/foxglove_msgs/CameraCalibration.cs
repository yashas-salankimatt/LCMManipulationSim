/* LCM type definition class file
 * This file was automatically generated by lcm-gen 1.5.1
 * DO NOT MODIFY BY HAND!!!!
 */

using System;
using System.Collections.Generic;
using System.IO;
using LCM.LCM;
 
namespace foxglove_msgs
{
    public sealed class CameraCalibration : LCM.LCM.LCMEncodable
    {
        public int d_length;
        public builtin_interfaces.Time timestamp;
        public String frame_id;
        public int width;
        public int height;
        public String distortion_model;
        public double[] d;
        public double[] k;
        public double[] r;
        public double[] p;
 
        public CameraCalibration()
        {
            k = new double[9];
            r = new double[9];
            p = new double[12];
        }
 
        public static readonly ulong LCM_FINGERPRINT;
        public static readonly ulong LCM_FINGERPRINT_BASE = 0x89c275083a857ce2L;
 
        static CameraCalibration()
        {
            LCM_FINGERPRINT = _hashRecursive(new List<String>());
        }
 
        public static ulong _hashRecursive(List<String> classes)
        {
            if (classes.Contains("foxglove_msgs.CameraCalibration"))
                return 0L;
 
            classes.Add("foxglove_msgs.CameraCalibration");
            ulong hash = LCM_FINGERPRINT_BASE
                 + builtin_interfaces.Time._hashRecursive(classes)
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
            byte[] __strbuf = null;
            outs.Write(this.d_length); 
 
            this.timestamp._encodeRecursive(outs); 
 
            __strbuf = System.Text.Encoding.GetEncoding("US-ASCII").GetBytes(this.frame_id); outs.Write(__strbuf.Length+1); outs.Write(__strbuf, 0, __strbuf.Length); outs.Write((byte) 0); 
 
            outs.Write(this.width); 
 
            outs.Write(this.height); 
 
            __strbuf = System.Text.Encoding.GetEncoding("US-ASCII").GetBytes(this.distortion_model); outs.Write(__strbuf.Length+1); outs.Write(__strbuf, 0, __strbuf.Length); outs.Write((byte) 0); 
 
            for (int a = 0; a < this.d_length; a++) {
                outs.Write(this.d[a]); 
            }
 
            for (int a = 0; a < 9; a++) {
                outs.Write(this.k[a]); 
            }
 
            for (int a = 0; a < 9; a++) {
                outs.Write(this.r[a]); 
            }
 
            for (int a = 0; a < 12; a++) {
                outs.Write(this.p[a]); 
            }
 
        }
 
        public CameraCalibration(byte[] data) : this(new LCMDataInputStream(data))
        {
        }
 
        public CameraCalibration(LCMDataInputStream ins)
        {
            if ((ulong) ins.ReadInt64() != LCM_FINGERPRINT)
                throw new System.IO.IOException("LCM Decode error: bad fingerprint");
 
            _decodeRecursive(ins);
        }
 
        public static foxglove_msgs.CameraCalibration _decodeRecursiveFactory(LCMDataInputStream ins)
        {
            foxglove_msgs.CameraCalibration o = new foxglove_msgs.CameraCalibration();
            o._decodeRecursive(ins);
            return o;
        }
 
        public void _decodeRecursive(LCMDataInputStream ins)
        {
            byte[] __strbuf = null;
            this.d_length = ins.ReadInt32();
 
            this.timestamp = builtin_interfaces.Time._decodeRecursiveFactory(ins);
 
            __strbuf = new byte[ins.ReadInt32()-1]; ins.ReadFully(__strbuf); ins.ReadByte(); this.frame_id = System.Text.Encoding.GetEncoding("US-ASCII").GetString(__strbuf);
 
            this.width = ins.ReadInt32();
 
            this.height = ins.ReadInt32();
 
            __strbuf = new byte[ins.ReadInt32()-1]; ins.ReadFully(__strbuf); ins.ReadByte(); this.distortion_model = System.Text.Encoding.GetEncoding("US-ASCII").GetString(__strbuf);
 
            this.d = new double[(int) d_length];
            for (int a = 0; a < this.d_length; a++) {
                this.d[a] = ins.ReadDouble();
            }
 
            this.k = new double[(int) 9];
            for (int a = 0; a < 9; a++) {
                this.k[a] = ins.ReadDouble();
            }
 
            this.r = new double[(int) 9];
            for (int a = 0; a < 9; a++) {
                this.r[a] = ins.ReadDouble();
            }
 
            this.p = new double[(int) 12];
            for (int a = 0; a < 12; a++) {
                this.p[a] = ins.ReadDouble();
            }
 
        }
 
        public foxglove_msgs.CameraCalibration Copy()
        {
            foxglove_msgs.CameraCalibration outobj = new foxglove_msgs.CameraCalibration();
            outobj.d_length = this.d_length;
 
            outobj.timestamp = this.timestamp.Copy();
 
            outobj.frame_id = this.frame_id;
 
            outobj.width = this.width;
 
            outobj.height = this.height;
 
            outobj.distortion_model = this.distortion_model;
 
            outobj.d = new double[(int) d_length];
            for (int a = 0; a < this.d_length; a++) {
                outobj.d[a] = this.d[a];
            }
 
            outobj.k = new double[(int) 9];
            for (int a = 0; a < 9; a++) {
                outobj.k[a] = this.k[a];
            }
 
            outobj.r = new double[(int) 9];
            for (int a = 0; a < 9; a++) {
                outobj.r[a] = this.r[a];
            }
 
            outobj.p = new double[(int) 12];
            for (int a = 0; a < 12; a++) {
                outobj.p[a] = this.p[a];
            }
 
            return outobj;
        }
    }
}

