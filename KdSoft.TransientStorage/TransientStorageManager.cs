using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime;

namespace KdSoft.Services.StorageServices.Transient
{
  /// <summary>Transient storage specific store manager.</summary>
  /// <remarks>Exposes transient storage specific methods.</remarks>
  public class TransientStorageManager: StorageManager<TransientStore>, IDisposable
  {
    Timer checkTimer;
    int lastMemoryCheck;
    bool memoryLow = false;
    object checkTimerObj = new object();

    public TransientStorageManager() : base() {
      // default values
      LockCheckWait = TimeSpan.FromMilliseconds(500);
      MemoryCheckPeriod = TimeSpan.FromSeconds(10);
      lastMemoryCheck = Environment.TickCount;
      checkTimer = new Timer(CheckTimerHandler, null, LockCheckWait, LockCheckWait);
    }

    void CheckTimerHandler(object state) {
      if (!Monitor.TryEnter(checkTimerObj))
        return;
      try {
        memoryLow = !ProcessCheckTimer();
        if (memoryLow) {
          GC.Collect();
#if NET45
                    memoryLow = !CheckMemory();
#endif
        }
      }
      finally {
        Monitor.Exit(checkTimerObj);
      }
    }

#if NET45
        bool CheckMemory() {
            bool result = true;
            try {
                // Let's try to determine if we have enough memory available. Here we assume
                // each entry in a property storage dictionary uses 16 bytes, so if we have
                // 2^20 (1M) entries, we use 16 MBytes and if the dictionary re-hashes it will
                // concurrently allocate at least twice its size (for the next size increase), so
                // for 2^20 (1M = 1048576) entries we need to be able to allocate at least 32 MBytes.
                int requiredMemory = EntryCount * 32 / 1048576;
                if (requiredMemory < 2)
                    requiredMemory = 2;
                using (new MemoryFailPoint(requiredMemory)) { }  // very slow
            }
            catch (InsufficientMemoryException) {
                result = false;
            }
            lastMemoryCheck = Environment.TickCount;
            return result;
        }
#endif

    bool ProcessCheckTimer() {
      ProcessCheckEvent();
#if NET45
            // check memory (once in a while) and trim the wait queue
            TimeSpan elapsed = new TimeSpan(0, 0, 0, 0, (int)(Environment.TickCount - lastMemoryCheck));
            if (elapsed > MemoryCheckPeriod) {  // consider possible overflow condition
                // TrimMemory();
                return CheckMemory();
            }
            else
                return true;
#else
      return true;
#endif
    }

    #region Public API

    public TimeSpan LockCheckWait { get; set; }

    public TimeSpan MemoryCheckPeriod { get; set; }

    public void ProcessCheckEvent() {
      TransientStore[] storeList;
      lock (storeLock) {
        storeList = new TransientStore[stores.Count];
        stores.Values.CopyTo(storeList, 0);
      }

      for (int indx = 0; indx < storeList.Length; indx++) {
        var store = storeList[indx];
        store.ProcessLockWaitQueue();
        store.ProcessTimeOuts();
      }
    }

    public int EntryCount {
      get {
        int result = 0;
        lock (storeLock) {
          foreach (KeyValuePair<string, TransientStore> entry in stores) {
            result += entry.Value.EntryCount;
          }
        }
        return result;
      }
    }

    #endregion

    #region IDisposable Members

    public void Dispose() {
      try {
        CloseStores();
      }
      finally {
        checkTimer.Dispose();
      }
    }

    #endregion
  }
}
