namespace KdSoft.Services.StorageServices
{
  /// <summary>
  /// Utility methods.
  /// </summary>
  public static class Util
  {
    /// <summary>
    /// Generic hash function, based on https://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
    /// </summary>
    /// <param name="data">Byte array to hash.</param>
    /// <returns>Hash value.</returns>
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
