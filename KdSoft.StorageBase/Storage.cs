
namespace KdSoft.Services.StorageServices
{
  /// <summary>
  /// Describes stored property.
  /// </summary>
  public struct PropDesc
  {
    /// <summary></summary>
    public readonly string Name;
    /// <summary></summary>
    public readonly string TypeId;

    public PropDesc(string name, string typeId) {
      Name = name;
      TypeId = typeId;
    }
  }
}
