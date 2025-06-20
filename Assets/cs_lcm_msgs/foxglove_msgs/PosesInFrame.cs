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
    public sealed class PosesInFrame : LCM.LCM.LCMEncodable
    {
        public int poses_length;
        public builtin_interfaces.Time timestamp;
        public String frame_id;
        public geometry_msgs.Pose[] poses;
 
        public PosesInFrame()
        {
        }
 
        public static readonly ulong LCM_FINGERPRINT;
        public static readonly ulong LCM_FINGERPRINT_BASE = 0x65f6cee7d8076f4bL;
 
        static PosesInFrame()
        {
            LCM_FINGERPRINT = _hashRecursive(new List<String>());
        }
 
        public static ulong _hashRecursive(List<String> classes)
        {
            if (classes.Contains("foxglove_msgs.PosesInFrame"))
                return 0L;
 
            classes.Add("foxglove_msgs.PosesInFrame");
            ulong hash = LCM_FINGERPRINT_BASE
                 + builtin_interfaces.Time._hashRecursive(classes)
                 + geometry_msgs.Pose._hashRecursive(classes)
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
            outs.Write(this.poses_length); 
 
            this.timestamp._encodeRecursive(outs); 
 
            __strbuf = System.Text.Encoding.GetEncoding("US-ASCII").GetBytes(this.frame_id); outs.Write(__strbuf.Length+1); outs.Write(__strbuf, 0, __strbuf.Length); outs.Write((byte) 0); 
 
            for (int a = 0; a < this.poses_length; a++) {
                this.poses[a]._encodeRecursive(outs); 
            }
 
        }
 
        public PosesInFrame(byte[] data) : this(new LCMDataInputStream(data))
        {
        }
 
        public PosesInFrame(LCMDataInputStream ins)
        {
            if ((ulong) ins.ReadInt64() != LCM_FINGERPRINT)
                throw new System.IO.IOException("LCM Decode error: bad fingerprint");
 
            _decodeRecursive(ins);
        }
 
        public static foxglove_msgs.PosesInFrame _decodeRecursiveFactory(LCMDataInputStream ins)
        {
            foxglove_msgs.PosesInFrame o = new foxglove_msgs.PosesInFrame();
            o._decodeRecursive(ins);
            return o;
        }
 
        public void _decodeRecursive(LCMDataInputStream ins)
        {
            byte[] __strbuf = null;
            this.poses_length = ins.ReadInt32();
 
            this.timestamp = builtin_interfaces.Time._decodeRecursiveFactory(ins);
 
            __strbuf = new byte[ins.ReadInt32()-1]; ins.ReadFully(__strbuf); ins.ReadByte(); this.frame_id = System.Text.Encoding.GetEncoding("US-ASCII").GetString(__strbuf);
 
            this.poses = new geometry_msgs.Pose[(int) poses_length];
            for (int a = 0; a < this.poses_length; a++) {
                this.poses[a] = geometry_msgs.Pose._decodeRecursiveFactory(ins);
            }
 
        }
 
        public foxglove_msgs.PosesInFrame Copy()
        {
            foxglove_msgs.PosesInFrame outobj = new foxglove_msgs.PosesInFrame();
            outobj.poses_length = this.poses_length;
 
            outobj.timestamp = this.timestamp.Copy();
 
            outobj.frame_id = this.frame_id;
 
            outobj.poses = new geometry_msgs.Pose[(int) poses_length];
            for (int a = 0; a < this.poses_length; a++) {
                outobj.poses[a] = this.poses[a].Copy();
            }
 
            return outobj;
        }
    }
}

