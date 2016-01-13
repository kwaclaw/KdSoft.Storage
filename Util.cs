using System;

namespace KdSoft.Services.StorageServices
{
    public delegate void IceExceptionCallback(Exception ex);

    public static class Util
    {
        // Taken from http://home.comcast.net/~bretm/hash/6.html with small modification
        public static uint FNVHash(byte[] data) {
            const uint p = 16777619;
            uint hash = 2166136261;
            for (int indx = 0; indx < data.Length; indx++)
                hash = (hash ^ data[indx]) * p;
            hash += hash << 13;
            hash ^= hash >> 7;
            hash += hash << 3;
            hash ^= hash >> 17;
            hash += hash << 5;
            return hash;
        }
    }
}
