using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace KdSoft.Services.StorageServices.Transient
{
  /// <summary>
  /// Implements transient (non-persistent) storage functions.
  /// </summary>
  public class TransientStore : Store<TransientStore>
  {
    ConcurrentDictionary<byte[], KeyEntry> propStore;
    TimeSpan timeOut;
    TimeSpan lockTimeOut;
    int lockId;
    static ArraySegment<PropEntry> emptyValues = new ArraySegment<PropEntry>(new PropEntry[0]);

    ConcurrentQueue<TimeOutEntry> timeoutQueue;
    ConcurrentQueue<Action> lockWaitQueue;

    public TransientStore(
      TransientStorageManager storeMgr,
      string name,
      PropDesc[] propDescs,
      int timeOut,
      int lockTimeOut)
      : base(storeMgr, name, propDescs)
    {
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
      out int resultCount)
    {
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
      TaskCompletionSource<GetResult> tcs)
    {
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
      if (entry == null)
        tcs.TrySetResult(ErrorCode.DoesNotExist);
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
      out bool result)
    {
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
      out int resultCount)
    {
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
      TaskCompletionSource<RemoveResult> tcs)
    {
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
    public bool Create(byte[] key) {
      KeyEntry entry = new KeyEntry(key, PropDescs.Length);
      bool result = propStore.TryAdd(key, entry);
      if (result)
        RestartTimer(entry);
      return result;
    }

    public Tuple<bool, int> Exists(byte[] key) {
      int timeLeft = 0;
      bool success = Exists(key, ref timeLeft);
      return new Tuple<bool, int>(success, timeLeft);
    }

    #endregion

    #region StoreOperations_ and StoreAdminOperations_ Members

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

    public class GetResult
    {
      public ErrorCode Status { get; internal set; }
      public ArraySegment<PropEntry> Values { get; internal set; }
    }

    public Task<GetResult> GetAsync(
      byte[] key,
      ArraySegment<PropRequest> requests,
      int maxWaitTime,
      bool force,
      object asyncState = null,
      TaskCreationOptions taskOptions = TaskCreationOptions.None)
    {
      int timeStamp = Environment.TickCount;
      TimeSpan maxWait = maxWaitTime == 0 ? TimeSpan.Zero : new TimeSpan(0, 0, maxWaitTime);
      var tcs = new TaskCompletionSource<GetResult>(asyncState, taskOptions);
      Get(key, requests, timeStamp, maxWait, force, tcs);
      return tcs.Task;
    }

    public Task<ErrorCode> PutAsync(
      byte[] key,
      ArraySegment<PropEntry> values,
      object asyncState = null,
      TaskCreationOptions taskOptions = TaskCreationOptions.None)
    {
      var tcs = new TaskCompletionSource<ErrorCode>(asyncState, taskOptions);
      Put(key, values, tcs);
      return tcs.Task;
    }

    public class DeleteResult
    {
      public ErrorCode Status { get; internal set; }
      public bool Deleted { get; internal set; }
    }

    public Task<DeleteResult> DeleteAsync(
      byte[] key,
      int maxWaitTime,
      bool force,
      object asyncState = null,
      TaskCreationOptions taskOptions = TaskCreationOptions.None)
    {
      int timeStamp = Environment.TickCount;
      TimeSpan maxWait = maxWaitTime == 0 ? TimeSpan.Zero : new TimeSpan(0, 0, maxWaitTime);
      var tcs = new TaskCompletionSource<DeleteResult>(asyncState, taskOptions);
      Delete(key, timeStamp, maxWait, force, tcs);
      return tcs.Task;
    }

    public class RemoveResult
    {
      public ErrorCode Status { get; internal set; }
      public ArraySegment<PropEntry> Values { get; internal set; }
    }

    public Task<RemoveResult> RemoveAsync(
      byte[] key,
      int maxWaitTime,
      bool force,
      object asyncState = null,
      TaskCreationOptions taskOptions = TaskCreationOptions.None) 
    {
      int timeStamp = Environment.TickCount;
      TimeSpan maxWait = maxWaitTime == 0 ? TimeSpan.Zero : new TimeSpan(0, 0, maxWaitTime);
      var tcs = new TaskCompletionSource<RemoveResult>(asyncState, taskOptions);
      Remove(key, timeStamp, maxWait, force, tcs);
      return tcs.Task;
    }

    #endregion

    #region StoreAdminBaseOperations_ Members

    public void ClearStore() {
      Clear();
    }

    public void RemoveStore() {
      Remove();
    }

    #endregion

    #region Nested Types

    // without this we could not compare byte arrays based on their content
    class KeyComparer : IEqualityComparer<byte[]>
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
