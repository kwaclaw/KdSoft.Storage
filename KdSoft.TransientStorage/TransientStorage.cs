
namespace KdSoft.Services.StorageServices.Transient
{
  public enum LockMode
  {
    None,
    Create,
    Read,
    Update
  }

  public enum ErrorCode
  {
    None = 0,
    General,
    DoesNotExist,
    AlreadyExists,
    CapacityExceeded,
    LockWaitTimeOut,
    InvalidLock,
    LockIdMismatch,
    Locked,
    NotLocked
  }

  public struct PropRequest
  {
    public readonly int Index;

    public readonly LockMode Mode;

    public PropRequest(int index, LockMode mode) {
      Index = index;
      Mode = mode;
    }
  }

  public struct PropEntry
  {
    public readonly int Index;

    public readonly int LockId;

    public byte[] Value;

    public PropEntry(int index, int lockId, byte[] value) {
      Index = index;
      LockId = lockId;
      Value = value;
    }

    public PropEntry(int index, int lockId) : this(index, lockId, null) { }
  }
}