
namespace KdSoft.Services.StorageServices
{
  public struct PropDesc
  {
    public readonly string Name;
    public readonly string TypeId;

    public PropDesc(string name, string typeId) {
      Name = name;
      TypeId = typeId;
    }
  }
}
