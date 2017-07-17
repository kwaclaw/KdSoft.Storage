
namespace KdSoft.Services.StorageServices.Transient
{
  /// <summary>
  /// Indicates how the property is locked. There are two types of lock: read and read/write.
  /// <list type="bullet">
  ///   <item><description>A read lock prevents read/write locks from being acquired as long as
  ///     it is active, but allows concurrent read-locks requested by other clients.</description></item>
  ///   <item><description>A read/write lock prevents all other locks from being acquired as long as
  ///     is active, thus serializing updates.A create lock is like a read/write lock,
  ///     except that it indicates that the current property value is not returned.</description></item>
  /// </list>
  /// </summary>
  public enum LockMode
  {
    None,
    Create,
    Read,
    Update
  }

  /// <summary>Error codes that can be returned by the API.</summary>
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

  /// <summary>
  /// Property access request. Identifies property within the stored value by index and specifies lock mode.
  /// </summary>
  public struct PropRequest
  {
    /// <summary />
    public readonly int Index;

    /// <summary />
    public readonly LockMode Mode;

    public PropRequest(int index, LockMode mode) {
      Index = index;
      Mode = mode;
    }
  }

  /// <summary>
  /// Property entry in stored value.
  /// </summary>
  public struct PropEntry
  {
    /// <summary>Index of property entry within stored value.</summary>
    public readonly int Index;

    /// <summary>Property lock identifier.</summary>
    public readonly int LockId;

    /// <summary>Value of property entry. Modifiable.</summary>
    public byte[] Value;

    public PropEntry(int index, int lockId, byte[] value) {
      Index = index;
      LockId = lockId;
      Value = value;
    }

    public PropEntry(int index, int lockId) : this(index, lockId, null) { }
  }
}