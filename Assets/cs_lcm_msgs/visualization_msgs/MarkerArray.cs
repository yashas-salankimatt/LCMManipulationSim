/* LCM type definition class file
 * This file was automatically generated by lcm-gen 1.5.1
 * DO NOT MODIFY BY HAND!!!!
 */

using System;
using System.Collections.Generic;
using System.IO;
using LCM.LCM;
 
namespace visualization_msgs
{
    public sealed class MarkerArray : LCM.LCM.LCMEncodable
    {
        public int markers_length;
        public visualization_msgs.Marker[] markers;
 
        public MarkerArray()
        {
        }
 
        public static readonly ulong LCM_FINGERPRINT;
        public static readonly ulong LCM_FINGERPRINT_BASE = 0xd9e3851dad1e0d9eL;
 
        static MarkerArray()
        {
            LCM_FINGERPRINT = _hashRecursive(new List<String>());
        }
 
        public static ulong _hashRecursive(List<String> classes)
        {
            if (classes.Contains("visualization_msgs.MarkerArray"))
                return 0L;
 
            classes.Add("visualization_msgs.MarkerArray");
            ulong hash = LCM_FINGERPRINT_BASE
                 + visualization_msgs.Marker._hashRecursive(classes)
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
            outs.Write(this.markers_length); 
 
            for (int a = 0; a < this.markers_length; a++) {
                this.markers[a]._encodeRecursive(outs); 
            }
 
        }
 
        public MarkerArray(byte[] data) : this(new LCMDataInputStream(data))
        {
        }
 
        public MarkerArray(LCMDataInputStream ins)
        {
            if ((ulong) ins.ReadInt64() != LCM_FINGERPRINT)
                throw new System.IO.IOException("LCM Decode error: bad fingerprint");
 
            _decodeRecursive(ins);
        }
 
        public static visualization_msgs.MarkerArray _decodeRecursiveFactory(LCMDataInputStream ins)
        {
            visualization_msgs.MarkerArray o = new visualization_msgs.MarkerArray();
            o._decodeRecursive(ins);
            return o;
        }
 
        public void _decodeRecursive(LCMDataInputStream ins)
        {
            this.markers_length = ins.ReadInt32();
 
            this.markers = new visualization_msgs.Marker[(int) markers_length];
            for (int a = 0; a < this.markers_length; a++) {
                this.markers[a] = visualization_msgs.Marker._decodeRecursiveFactory(ins);
            }
 
        }
 
        public visualization_msgs.MarkerArray Copy()
        {
            visualization_msgs.MarkerArray outobj = new visualization_msgs.MarkerArray();
            outobj.markers_length = this.markers_length;
 
            outobj.markers = new visualization_msgs.Marker[(int) markers_length];
            for (int a = 0; a < this.markers_length; a++) {
                outobj.markers[a] = this.markers[a].Copy();
            }
 
            return outobj;
        }
    }
}

