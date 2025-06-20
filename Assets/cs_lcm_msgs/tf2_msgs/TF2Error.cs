/* LCM type definition class file
 * This file was automatically generated by lcm-gen 1.5.1
 * DO NOT MODIFY BY HAND!!!!
 */

using System;
using System.Collections.Generic;
using System.IO;
using LCM.LCM;
 
namespace tf2_msgs
{
    public sealed class TF2Error : LCM.LCM.LCMEncodable
    {
        public byte error;
        public String error_string;
 
        public TF2Error()
        {
        }
 
        public static readonly ulong LCM_FINGERPRINT;
        public static readonly ulong LCM_FINGERPRINT_BASE = 0x1e8ff0d80d02ee55L;
 
        public const int NO_ERROR = 0;
        public const int LOOKUP_ERROR = 1;
        public const int CONNECTIVITY_ERROR = 2;
        public const int EXTRAPOLATION_ERROR = 3;
        public const int INVALID_ARGUMENT_ERROR = 4;
        public const int TIMEOUT_ERROR = 5;
        public const int TRANSFORM_ERROR = 6;

        static TF2Error()
        {
            LCM_FINGERPRINT = _hashRecursive(new List<String>());
        }
 
        public static ulong _hashRecursive(List<String> classes)
        {
            if (classes.Contains("tf2_msgs.TF2Error"))
                return 0L;
 
            classes.Add("tf2_msgs.TF2Error");
            ulong hash = LCM_FINGERPRINT_BASE
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
            outs.Write(this.error); 
 
            __strbuf = System.Text.Encoding.GetEncoding("US-ASCII").GetBytes(this.error_string); outs.Write(__strbuf.Length+1); outs.Write(__strbuf, 0, __strbuf.Length); outs.Write((byte) 0); 
 
        }
 
        public TF2Error(byte[] data) : this(new LCMDataInputStream(data))
        {
        }
 
        public TF2Error(LCMDataInputStream ins)
        {
            if ((ulong) ins.ReadInt64() != LCM_FINGERPRINT)
                throw new System.IO.IOException("LCM Decode error: bad fingerprint");
 
            _decodeRecursive(ins);
        }
 
        public static tf2_msgs.TF2Error _decodeRecursiveFactory(LCMDataInputStream ins)
        {
            tf2_msgs.TF2Error o = new tf2_msgs.TF2Error();
            o._decodeRecursive(ins);
            return o;
        }
 
        public void _decodeRecursive(LCMDataInputStream ins)
        {
            byte[] __strbuf = null;
            this.error = ins.ReadByte();
 
            __strbuf = new byte[ins.ReadInt32()-1]; ins.ReadFully(__strbuf); ins.ReadByte(); this.error_string = System.Text.Encoding.GetEncoding("US-ASCII").GetString(__strbuf);
 
        }
 
        public tf2_msgs.TF2Error Copy()
        {
            tf2_msgs.TF2Error outobj = new tf2_msgs.TF2Error();
            outobj.error = this.error;
 
            outobj.error_string = this.error_string;
 
            return outobj;
        }
    }
}

