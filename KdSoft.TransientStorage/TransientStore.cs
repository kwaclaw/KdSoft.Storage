using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KdSoft.Services.StorageServices.Transient
{
  /// <summary>
  /// Implements transient (non-persistent) storage functions.
  /// </summary>
  public class TransientStore: Store<TransientStore>
  {
    readonly ConcurrentDictionary<byte[], KeyEntry> propStore;
    TimeSpan timeOut;
    TimeSpan lockTimeOut;
    int lockId;
    static ArraySegment<PropEntry> emptyValues = new ArraySegment<PropEntry>(new PropEntry[0]);

    readonly ConcurrentQueue<TimeOutEntry> timeoutQueue;
    readonly ConcurrentQueue<Action> lockWaitQueue;

    public TransientStore(
      TransientStorageManager storeMgr,
      string name,
      PropDesc[] propDescs,
      int timeOut,
      int lockTimeOut)
      : base(storeMgr, name, propDescs
    ) {
      TimeOut = new TimeSpan(0, 0, timeOut);
      LockTimeOut = new TimeSpan(0, 0, lockTimeOut);
      lockId = 0;
      timeoutQueue = new ConcurrentQueue<TimeOutEntry>();
      lockWaitQueue = new ConcurrentQueue<Action>();
      propStore = new ConcurrentDictionary<byte[], KeyEntry>(4, 4096, new KeyComparer());
    }

    int NextLockId() {
      return Interlocked.Increment(ref lockId);
    }

    protected override void Close() {
      Clear();
    }

    #region Exposed to TransientStorageManager

    // this works propertly only if the lock timeout is significantly shorter than
    // the key timeout, otherwise an entry could be locked while timing out;
    // it also requires that every time a lock is acquired, the timeout for the entry is restarted!
    internal void ProcessTimeOuts() {
      int currentTicks = Environment.TickCount;
      while (RemoveTimeOutEntry(currentTicks)) ;
    }

    // returns if the last time-out record was removed (not if an entry was removed!)
    bool RemoveTimeOutEntry(int currentTicks) {
      TimeOutEntry tr;
      bool result = timeoutQueue.TryPeek(out tr);
      if (!result)
        return result;
      // subtraction is immune to rollover as long as the difference is less than Int32.MaxValue,
      // which is 49 days, given that Environment.TickCount represents milliseconds
      TimeSpan elapsed = new TimeSpan(0, 0, 0, 0, (int)(currentTicks - tr.TimeStamp));
      result = elapsed >= timeOut;
      if (!result)
        return result;
      // the timeout record is expired, but not necessarily the propStore entry
      try {
        result = timeoutQueue.TryDequeue(out tr);
      }
      finally {
        KeyEntry entry;
        // the propStore entry is only expired if its timestamp matches the timeout record;
        // also, if the entry referenced by this time-out record was already deleted, there
        // might be a new entry with the same key which we must not remove yet!
        if (result) lock (tr.Entry) {
            if (tr.Entry.TimeStamp == tr.TimeStamp && !tr.Entry.IsDeleted)
              propStore.TryRemove(tr.Entry.Key, out entry);
          }
      }
      return result;
    }

    // add waiting GetTask instances back to input queue
    internal void ProcessLockWaitQueue() {
      Action retry;
      while (lockWaitQueue.TryDequeue(out retry)) {
        retry();
      }
    }

    internal int EntryCount {
      get { return propStore.Count; }
    }

    #endregion

    #region Implementation

    void RestartTimer(KeyEntry entry) {
      int currentTicks = Environment.TickCount;
      RemoveTimeOutEntry(currentTicks);
      timeoutQueue.Enqueue(new TimeOutEntry(entry, currentTicks));
    }

    bool Exists(byte[] key, ref int timeLeft) {
      KeyEntry entry;
      bool result = propStore.TryGetValue(key, out entry);
      if (result) lock (entry) {
          TimeSpan spanLeft = new TimeSpan(0, 0, 0, 0, (int)(Environment.TickCount - entry.TimeStamp));
          timeLeft = spanLeft.Seconds;
        }
      return result;
    }

    KeyEntry GetEntry(byte[] key) {
      KeyEntry result;
      if (!propStore.TryGetValue(key, out result))
        result = null;
      return result;
    }

    KeyEntry NewKeyEntry(byte[] key) {
      KeyEntry result = new KeyEntry(key, PropDescs.Length);
      RestartTimer(result);
      return result;
    }

    // the result will contain as many entries as there are valid requests
    ErrorCode Get(
      byte[] key,
      ArraySegment<PropRequest> requests,
      int timeStamp,
      TimeSpan maxWaitTime,
      bool force,
      ref PropEntry[] result,
      out int resultCount
    ) {
      ErrorCode status = ErrorCode.None;
      resultCount = 0;
      // makes sure an entry for the key exists, restarts the timer and returns the entry
      KeyEntry entry = propStore.GetOrAdd(key, NewKeyEntry);
      lock (entry) {
        int lockedCount = entry.GetLockedCount(requests, LockTimeOut);
        bool locksChecked = lockedCount == 0;
        if (locksChecked)
          entry.LockAndGet(requests, NextLockId(), ref result, out resultCount);
        else {
          bool waitExpired = maxWaitTime == TimeSpan.Zero;
          if (!waitExpired) {
            TimeSpan elapsed = new TimeSpan(0, 0, 0, 0, (int)(Environment.TickCount - timeStamp));
            waitExpired = elapsed > maxWaitTime;
          }
          if (waitExpired) {  // on wait expiry, force locks or throw error
            if (force)
              entry.LockAndGet(requests, NextLockId(), ref result, out resultCount);
            else {
              status = ErrorCode.LockWaitTimeOut;
            }
          }
          else
            status = ErrorCode.Locked;
        }
      }
      return status;
    }

    void Get(
      byte[] key,
      ArraySegment<PropRequest> requests,
      int timeStamp,
      TimeSpan maxWait,
      bool force,
      TaskCompletionSource<GetResult> tcs
    ) {
      try {
        PropEntry[] results = null;
        int resultCount;
        ErrorCode status = Get(key, requests, timeStamp, maxWait, force, ref results, out resultCount);
        if (status == ErrorCode.Locked) {
          Action retry = () => { Get(key, requests, timeStamp, maxWait, force, tcs); };
          lockWaitQueue.Enqueue(retry);
        }
        else {
          var getResult = new GetResult();
          if (resultCount == 0)
            getResult.Values = emptyValues;
          else
            getResult.Values = new ArraySegment<PropEntry>(results, 0, resultCount);
          getResult.Status = status;
          tcs.TrySetResult(getResult);
        }
      }
      catch (Exception ex) {
        tcs.TrySetException(ex);
      }
    }

    void Put(byte[] key, ArraySegment<PropEntry> values, TaskCompletionSource<ErrorCode> tcs) {
      KeyEntry entry = GetEntry(key);
      if (entry == null) {
        tcs.TrySetResult(ErrorCode.DoesNotExist);
        return;
      }
      RestartTimer(entry);
      lock (entry) {
        tcs.TrySetResult(entry.Set(values));
      }
    }

    // will call entry.SetDeleted even if already deleted
    bool Delete(KeyEntry entry) {
      byte[] key = entry.Key;
      entry.SetDeleted();
      return propStore.TryRemove(key, out entry);
    }

    ErrorCode Delete(
      byte[] key,
      int timeStamp,
      TimeSpan maxWaitTime,
      bool force,
      out bool result
    ) {
      ErrorCode status = ErrorCode.None;
      KeyEntry entry = GetEntry(key);
      result = entry != null;
      if (result) lock (entry) {  // entry exists
          int lockedCount = entry.GetAllLockedCount(LockTimeOut);
          if (lockedCount == 0) {
            result = Delete(entry);
          }
          else {
            bool waitExpired = maxWaitTime == TimeSpan.Zero;
            if (!waitExpired) {
              TimeSpan elapsed = new TimeSpan(0, 0, 0, 0, (int)(Environment.TickCount - timeStamp));
              waitExpired = elapsed > maxWaitTime;
            }
            if (waitExpired) {  // on wait expiry, force deletion or throw error
              if (force)
                result = Delete(entry);
              else
                status = ErrorCode.LockWaitTimeOut;
            }
            else
              status = ErrorCode.Locked;
          }
        }
      return status;
    }

    void Delete(byte[] key, int timeStamp, TimeSpan maxWait, bool force, TaskCompletionSource<DeleteResult> tcs) {
      try {
        bool deleted;
        ErrorCode status = Delete(key, timeStamp, maxWait, force, out deleted);
        if (status == ErrorCode.Locked) {
          Action retry = () => { Delete(key, timeStamp, maxWait, force, tcs); };
          lockWaitQueue.Enqueue(retry);
        }
        else {
          var deleteResult = new DeleteResult();
          deleteResult.Deleted = deleted;
          deleteResult.Status = status;
          tcs.TrySetResult(deleteResult);
        }
      }
      catch (Exception ex) {
        tcs.TrySetException(ex);
      }
    }

    int RemoveEntry(KeyEntry entry, ref PropEntry[] props) {
      if (props == null || props.Length < entry.Length)
        props = new PropEntry[entry.Length];
      int count = entry.GetAll(props);
      if (Delete(entry))
        return count;
      else
        return -1;
    }

    ErrorCode Remove(
      byte[] key,
      int timeStamp,
      TimeSpan maxWaitTime,
      bool force,
      ref PropEntry[] results,
      out int resultCount
    ) {
      results = null;
      resultCount = 0;
      KeyEntry entry = GetEntry(key);
      if (entry == null)
        return ErrorCode.DoesNotExist;
      ErrorCode status = ErrorCode.None;
      lock (entry) {
        int lockedCount = entry.GetAllLockedCount(LockTimeOut);
        if (lockedCount == 0) {
          int removed = RemoveEntry(entry, ref results);
          if (removed < 0)
            status = ErrorCode.DoesNotExist;
          else
            resultCount = removed;
        }
        else {
          bool waitExpired = maxWaitTime == TimeSpan.Zero;
          if (!waitExpired) {
            TimeSpan elapsed = new TimeSpan(0, 0, 0, 0, (int)(Environment.TickCount - timeStamp));
            waitExpired = elapsed > maxWaitTime;
          }
          if (waitExpired) {  // on wait expiry, force deletion or throw error
            if (force) {
              int removed = RemoveEntry(entry, ref results);
              if (removed < 0)
                status = ErrorCode.DoesNotExist;
              else
                resultCount = removed;
            }
            else
              status = ErrorCode.LockWaitTimeOut;
          }
          else
            status = ErrorCode.Locked;
        }
      }
      return status;
    }

    void Remove(
      byte[] key,
      int timeStamp,
      TimeSpan maxWait,
      bool force,
      TaskCompletionSource<RemoveResult> tcs
    ) {
      try {
        PropEntry[] results = null;
        int resultCount;
        ErrorCode status = Remove(key, timeStamp, maxWait, force, ref results, out resultCount);
        if (status == ErrorCode.Locked) {
          Action retry = () => { Remove(key, timeStamp, maxWait, force, tcs); };
          lockWaitQueue.Enqueue(retry);
        }
        else {
          var removeResult = new RemoveResult();
          if (resultCount == 0)
            removeResult.Values = emptyValues;
          else
            removeResult.Values = new ArraySegment<PropEntry>(results, 0, resultCount);
          removeResult.Status = status;
          tcs.TrySetResult(removeResult);
        }
      }
      catch (Exception ex) {
        tcs.TrySetException(ex);
      }
    }

    void Clear() {
      propStore.Clear();
      TimeOutEntry tr;
      while (timeoutQueue.TryDequeue(out tr)) ;
      Action retry;
      while (lockWaitQueue.TryDequeue(out retry)) ;
    }

    #endregion

    #region StoreBaseOperations_ Members

    // does not check property indexes (Prop.Index) for allowed range!
    /// <summary>
    /// Creates new empty Value entry in store.
    /// </summary>
    /// <param name="key">Identifier for the entry.</param>
    /// <returns><see langword="true"/> if the entry could be created, <see langword="false"/> otherwise.</returns>
    /// <remarks></remarks>
    public bool Create(byte[] key) {
      KeyEntry entry = new KeyEntry(key, PropDescs.Length);
      bool result = propStore.TryAdd(key, entry);
      if (result)
        RestartTimer(entry);
      return result;
    }

    /// <summary>
    /// Checks if Value entry exists for the given key.
    /// </summary>
    /// <param name="key">Identifier for the entry.</param>
    /// <returns>Boolean value indicating if entry exists, and number of seconds left before entry times out (if applicable).</returns>
    public Tuple<bool, int> Exists(byte[] key) {
      int timeLeft = 0;
      bool success = Exists(key, ref timeLeft);
      return new Tuple<bool, int>(success, timeLeft);
    }

    #endregion

    #region StoreOperations_ and StoreAdminOperations_ Members

    /// <summary>
    /// Timeout for stored values. Must be at least twice the <see cref="LockTimeOut"/>.
    /// </summary>
    public TimeSpan TimeOut {
      get { return timeOut; }
      set {
        if (value < TimeSpan.Zero)
          throw new ArgumentOutOfRangeException("value", "Negative storage time-out value: " + value.Seconds + ".");
        if (value.Ticks < (2 * lockTimeOut.Ticks))
          throw new ArgumentOutOfRangeException("value", "Storage time-out must be at least twice the lock time-out.");
        timeOut = value;
      }
    }

    /// <summary>
    /// Lock timeout to use. Must be less than half of <see cref="Timeout"/>.
    /// </summary>
    public TimeSpan LockTimeOut {
      get { return lockTimeOut; }
      set {
        if (value < TimeSpan.Zero)
          throw new ArgumentOutOfRangeException("value", "Negative lock time-out value: " + value.Seconds + ".");
        if (timeOut.Ticks < (2 * value.Ticks))
          throw new ArgumentOutOfRangeException("value", "Lock time-out must be less than half the storage time-out.");
        lockTimeOut = value;
      }
    }

    /// <summary>
    /// Result holder for <see cref="GetAsync(byte[], ArraySegment{PropRequest}, int, bool, TaskCreationOptions)"/> requests.
    /// </summary>
    public class GetResult
    {
      /// <summary />
      public ErrorCode Status { get; internal set; }
      /// <summary />
      public ArraySegment<PropEntry> Values { get; internal set; }
    }

    /// <summary>
    ///   Returns those properties for the given key that match the requests passed.
    /// </summary>
    /// <param name="key">Key identifying the stored value.</param>
    /// <param name="requests">Property access requests. If the requests sequence is empty,
    ///   no properties are returned (empty sequence).</param>
    /// <param name="maxWaitTime">Maximum seconds to wait for locked properties to become available.
    ///   Triggers <see cref="ErrorCode.LockWaitTimeOut"/> on timeout. A value of 0 means: no waiting.</param>
    /// <param name="force">Determines behaviour on lock wait timeout, or when <paramref name="maxWaitTime"/> == 0:
    ///   If <see langword="true"/>, will lock all requested properties, even if they should block, and return successfully,
    ///   otherwise will trigger <see cref="ErrorCode.LockWaitTimeOut"/> if not all locked properties have become available.</param>
    /// <param name="taskOptions">Task creation options, if any.</param>
    /// <returns>Struct holding the retrieved property entries on success, or an error code on failure.</returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>Obtains a lock for each requested property based on its PropRequest.Mode value.
    ///     If this value is lmCreate then the property will be be locked for update,
    ///     but it's Value will not be returned. This can be used when the property will be
    ///     assigned for the first time, or when the current value is not needed.</description></item>
    ///   <item><description>The locks will expire (time out) according to how the storage is configured.
    ///     If a property lock has expired then it will be treated as if there is no lock.</description></item>
    ///   <item><description>Blocking means: active read locks block read/write lock requests, and active read/write
    ///     locks block all other lock requests.</description></item>
    ///   <item><description>When properties are read locked, a new read lock request will cause the old read
    ///     locks to be replaced (and thus released), and the new lock holder assumes the
    ///     responsibility to release the (continued) read lock. However, a read lock
    ///     cannot be acquired when a read/write lock is active, unless "force" is true.</description></item>
    ///   <item><description>During the wait time, no property locks will be accumulated, to prevent lock
    ///     contention and dead-locks. Locks will be checked periodically, as configured
    ///     (e.g. every 0.5 seconds). This means it is possible that the call will time out
    ///     even if no property was locked continuously for the whole waiting period.</description></item>
    ///   <item><description>If a requested property is not found (i.e. the requested index is out of range),
    ///     then it is not included in the result; so if  no requested properties are found
    ///     at all, an empty sequence is returned.</description></item>
    ///   <item><description>If no entry for the key exists, a new entry will be created (as if Create was called).</description></item>
    ///   <item><description>Starts the time-out countdown for the locks.</description></item>
    ///   <item><description>Restarts the time-out countdown for the entry.</description></item>
    /// </list>
    /// </remarks>
    public Task<GetResult> GetAsync(
      byte[] key,
      ArraySegment<PropRequest> requests,
      int maxWaitTime,
      bool force,
      TaskCreationOptions taskOptions = TaskCreationOptions.None
    ) {
      int timeStamp = Environment.TickCount;
      TimeSpan maxWait = maxWaitTime == 0 ? TimeSpan.Zero : new TimeSpan(0, 0, maxWaitTime);
      var tcs = new TaskCompletionSource<GetResult>(taskOptions);
      Get(key, requests, timeStamp, maxWait, force, tcs);
      return tcs.Task;
    }

    /// <summary>
    /// Stores/updates the property arguments in the entry for the given key and releases
    /// the locks for all properties passed as argument.Other properties remain locked.
    /// </summary>
    /// <param name="key">Key identifying the stored value.</param>
    /// <param name="values">Property entries to store in Value entry.</param>
    /// <param name="taskOptions">Task creation options, if any.</param>
    /// <returns><see cref="ErrorCode"/>indicating success or failure.</returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>All properties passed must have the same lock id that was previously used to lock
    ///     the property, except when the value is an empty sequence.
    ///     Otherwise a <see cref="ErrorCode.LockIdMismatch"/> will be returned.</description></item>
    ///   <item><description>If the Value field of a property is a non-empty byte sequence then the stored
    ///    property value will be updated.This requires that a read/write lock was previously obtained.
    ///    Otherwise a <see cref="ErrorCode.InvalidLock"/> will be returned.</description></item>
    ///   <item><description>If the Value field for a property is an empty sequence, then no update will be performed,
    ///     but the existing lock will be cleared if it's lock id matches the argument's lock id.</description></item>
    ///   <item><description>If the lock ids do not match then the action depends on the type of locks involved:
    ///     If both are read-locks, then it will be assumed that the existing lock has replaced
    ///     the argument's old lock, and no action is taken.
    ///     In any other case a <see cref="ErrorCode.LockIdMismatch"/> will be returned, as this
    ///     can only happen when a lock was forcibly replaced or cleared.</description></item>
    ///   <item><description>If a property lock has expired but not been replaced by another lock, that is, the
    ///     lock id still matches, then the update will succeed and no error will be reported.</description></item>
    ///   <item><description>If the property is not assigned (is empty) or not locked,
    ///     then a <see cref="ErrorCode.NotLocked"/> will be returned</description></item>
    ///   <item><description>If no entry for the key exists, then a <see cref="ErrorCode.DoesNotExist"/> will be returned</description></item>
    ///   <item><description>Restarts the time-out countdown for the entry.</description></item>
    /// </list>
    /// </remarks>
    public Task<ErrorCode> PutAsync(
      byte[] key,
      ArraySegment<PropEntry> values,
      TaskCreationOptions taskOptions = TaskCreationOptions.None
    ) {
      var tcs = new TaskCompletionSource<ErrorCode>(taskOptions);
      Put(key, values, tcs);
      return tcs.Task;
    }

    /// <summary>
    /// Result holder for <see cref="DeleteAsync(byte[], int, bool, TaskCreationOptions)"/> requests.
    /// </summary>
    public class DeleteResult
    {
      public ErrorCode Status { get; internal set; }
      public bool Deleted { get; internal set; }
    }

    /// <summary>
    /// Removes entry for the given key.
    /// </summary>
    /// <param name="key">Key identifying the stored value.</param>
    /// <param name="maxWaitTime">Maximum seconds to wait for locked properties to become available.
    ///   Triggers <see cref="ErrorCode.LockWaitTimeOut"/> on timeout. A value of 0 means: no waiting.</param>
    /// <param name="force">Determines behaviour on lock wait timeout, or when <paramref name="maxWaitTime"/> == 0:
    ///   If <see langword="true"/>, will remove the entry, otherwise will trigger <see cref="ErrorCode.LockWaitTimeOut"/>.</param>
    /// <param name="taskOptions">Task creation options, if any.</param>
    /// <returns>Struct holding a boolean that indicates if the entry was indeed
    ///   removed (it may not have existed), or an error code on failure.</returns>
    /// <remarks>
    ///   <list type="bullet">
    ///   <item><description>A Remove request is blocked by read as well as read/write locks.</description></item>
    ///   <item><description>When an entry is forcibly removed, some properties may still have valid locks.</description></item>
    ///   </list>
    /// </remarks>
    public Task<DeleteResult> DeleteAsync(
      byte[] key,
      int maxWaitTime,
      bool force,
      TaskCreationOptions taskOptions = TaskCreationOptions.None
    ) {
      int timeStamp = Environment.TickCount;
      TimeSpan maxWait = maxWaitTime == 0 ? TimeSpan.Zero : new TimeSpan(0, 0, maxWaitTime);
      var tcs = new TaskCompletionSource<DeleteResult>(taskOptions);
      Delete(key, timeStamp, maxWait, force, tcs);
      return tcs.Task;
    }

    /// <summary>
    /// Result holder for <see cref="RemoveAsync(byte[], int, bool, TaskCreationOptions)"/> requests.
    /// </summary>
    public class RemoveResult
    {
      public ErrorCode Status { get; internal set; }
      public ArraySegment<PropEntry> Values { get; internal set; }
    }

    /// <summary>
    /// Removes entry for the given key, returning all properties that were stored,
    /// or an empty sequence if no properties were found.
    /// </summary>
    /// <param name="key">Key identifying the stored value.</param>
    /// <param name="maxWaitTime">Maximum seconds to wait for locked properties to become available.
    ///   Triggers <see cref="ErrorCode.LockWaitTimeOut"/> on timeout. A value of 0 means: no waiting.</param>
    /// <param name="force">Determines behaviour on lock wait timeout, or when <paramref name="maxWaitTime"/> == 0:
    ///   If <see langword="true"/>, will remove the entry, otherwise will trigger <see cref="ErrorCode.LockWaitTimeOut"/>.</param>
    /// <param name="taskOptions">Task creation options, if any.</param>
    /// <returns>Struct holding the removed property entries on success, or an error code on failure.</returns>
    /// <remarks>
    ///   <list type="bullet">
    ///   <item><description>A Remove request is blocked by read as well as read/write locks.</description></item>
    ///   <item><description>If no entry for the key exists, a <see cref="ErrorCode.DoesNotExist"/> is returned.</description></item>
    ///   <item><description>When an entry is forcibly removed, some properties may still have valid locks.</description></item>
    ///   </list>
    /// </remarks>
    public Task<RemoveResult> RemoveAsync(
      byte[] key,
      int maxWaitTime,
      bool force,
      TaskCreationOptions taskOptions = TaskCreationOptions.None
    ) {
      int timeStamp = Environment.TickCount;
      TimeSpan maxWait = maxWaitTime == 0 ? TimeSpan.Zero : new TimeSpan(0, 0, maxWaitTime);
      var tcs = new TaskCompletionSource<RemoveResult>(taskOptions);
      Remove(key, timeStamp, maxWait, force, tcs);
      return tcs.Task;
    }

    #endregion

    #region StoreAdminBaseOperations_ Members

    /// <summary>
    /// Clears store of all entries, without regard to ongoing transactions.
    /// Use with care, best when the store is not in use.
    /// </summary>
    public void ClearStore() {
      Clear();
    }

    /// <summary>
    /// Removes store from storage without regard to ongoing transactions.
    /// Use with care, best when the store is not in use.
    /// </summary>
    public void RemoveStore() {
      Remove();
    }

    #endregion

    #region Nested Types

    // without this we could not compare byte arrays based on their content
    class KeyComparer: IEqualityComparer<byte[]>
    {
      #region IEqualityComparer<byte[]> Members

      public bool Equals(byte[] x, byte[] y) {
        if (object.ReferenceEquals(x, y))
          return true;
        if ((object)x == null || (object)y == null)
          return false;
        if (x.Length != y.Length)
          return false;
        for (int indx = 0; indx < x.Length; indx++)
          if (x[indx] != y[indx])
            return false;
        return true;
      }

      public int GetHashCode(byte[] obj) {
        unchecked {
          return (int)Util.FNVHash(obj);
        }
      }

      #endregion
    }

    struct TimeOutEntry
    {
      public readonly int TimeStamp;
      public readonly KeyEntry Entry;

      public TimeOutEntry(KeyEntry entry, int timeStamp) {
        TimeStamp = timeStamp;
        Entry = entry;
        entry.TimeStamp = timeStamp;
      }
    }

    #endregion
  }
}
