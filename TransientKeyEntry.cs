using System;

namespace KdSoft.Services.StorageServices.Transient
{
    /// <summary>
    /// Represents an entry into the transient store.
    /// </summary>
    /// <remarks>This class is not thread-safe!. Any code using it must synchronize access.</remarks>
    class KeyEntry
    {
        public int TimeStamp;
        byte[] key;
        Prop[] props;

        public KeyEntry(byte[] key, int length) {
            this.key = key;
            props = new Prop[length];
            props.Initialize();
        }

        public byte[] Key
        {
            get { return key; }
        }

        public int Length
        {
            get { return props.Length; }
        }

        bool PropIsLocked(ref Prop prop, TimeSpan lockSpan) {
            // property is assigned && property is locked && lock is not expired
            return !prop.IsEmpty && !prop.Lock.IsOpen && !prop.Lock.IsExpired(lockSpan);
        }

        bool PropIsLocked(ref Prop prop, TimeSpan lockSpan, LockMode requestMode) {
            bool result = PropIsLocked(ref prop, lockSpan);
            if (result && requestMode == LockMode.Read)  // concurrent read-locks are allowed
                result = prop.Lock.Mode != LockMode.Read;
            return result;
        }

        // returns the indexes of requested locked properties;
        // the length of the result parameter must be sufficient (>= requests.Length)
        public int GetLocked(
          ArraySegment<PropRequest> requests,
          TimeSpan lockSpan,
          int[] result) {
            int resIndx = 0;
            int indxLimit = requests.Offset + requests.Count;
            for (int indx = requests.Offset; indx < indxLimit; indx++) {
                PropRequest request = requests.Array[indx];
                if (PropIsLocked(ref props[request.Index], lockSpan, request.Mode))
                    result[resIndx++] = request.Index;
            }
            return resIndx;
        }

        public int GetLockedCount(ArraySegment<PropRequest> requests, TimeSpan lockSpan) {
            int result = 0;
            int indxLimit = requests.Offset + requests.Count;
            for (int indx = requests.Offset; indx < indxLimit; indx++) {
                PropRequest request = requests.Array[indx];
                if (PropIsLocked(ref props[request.Index], lockSpan, request.Mode))
                    result++;
            }
            return result;
        }

        // Locks and returns all requested properties, even if already locked;
        // includes properties that have not been assigned yet (as they now have a lock);
        // the length of the result parameter must be sufficient (>= requests.Length)
        public void LockAndGet(
          ArraySegment<PropRequest> requests,
          int lockId,
          ref PropEntry[] result,
          out int resultCount) {
            if (result == null || result.Length < requests.Count)
                result = new PropEntry[requests.Count];
            int resIndx = 0;
            int indxLimit = requests.Offset + requests.Count;
            for (int indx = requests.Offset; indx < indxLimit; indx++) {
                PropRequest request = requests.Array[indx];
                if (request.Index < 0 || request.Index >= props.Length)
                    continue;
                if (props[request.Index].IsEmpty) {   // property was never assigned before
                    props[request.Index] = Prop.CreateProp(lockId, request.Mode); ;
                    result[resIndx] = new PropEntry(request.Index, lockId, null);
                }
                else {
                    props[request.Index].Lock = PropLock.StartLock(lockId, request.Mode);
                    result[resIndx] = new PropEntry( // don't return Value for create requests
                      request.Index, lockId, request.Mode == LockMode.Create ? null : props[request.Index].Value);
                }
                resIndx++;
            }
            resultCount = resIndx;
        }

        // returns the indexes of all locked properties;
        // the length of the result parameter must be sufficient (>= props.Length)
        public int GetAllLocked(TimeSpan lockSpan, ref int[] result) {
            int resIndx = 0;
            for (int indx = 0; indx < props.Length; indx++) {
                if (PropIsLocked(ref props[indx], lockSpan))
                    result[resIndx++] = indx;
            }
            return resIndx;
        }

        public int GetAllLockedCount(TimeSpan lockSpan) {
            int result = 0;
            for (int indx = 0; indx < props.Length; indx++) {
                if (PropIsLocked(ref props[indx], lockSpan))
                    result++;
            }
            return result;
        }

        private bool AssignPropEntry(int propIndex, ref Prop prop, ref PropEntry propEntry) {
            if (prop.IsEmpty)
                return false;
            else {
                propEntry = new PropEntry(propIndex, prop.Lock.Id, prop.Value);
                return true;
            }
        }

        // returns all properties with an assigned value (regardless of locking status);
        // the length of the result parameter must be sufficient (>= props.Length)
        public int GetAll(PropEntry[] result) {
            int resIndx = 0;
            for (int indx = 0; indx < props.Length; indx++) {
                if (AssignPropEntry(indx, ref props[indx], ref result[resIndx]))
                    resIndx++;
            }
            return resIndx;
        }

        ErrorCode CheckLockForUpdate(ref Prop prop, int lockId) {
            if (prop.IsEmpty || prop.Lock.IsOpen)
                return ErrorCode.NotLocked;
            if (prop.Lock.Id != lockId)
                return ErrorCode.LockIdMismatch;
            if (prop.Lock.Mode == LockMode.Read)
                return ErrorCode.InvalidLock;
            return ErrorCode.None;  // create or update
        }

        //TODO Should we really allow anyone to clear a read lock even if the id is not a match?
        ErrorCode CheckLockForClear(ref Prop prop, int lockId) {
            if (prop.Lock.Id != lockId && prop.Lock.Mode != LockMode.Read)
                return ErrorCode.LockIdMismatch;
            else
                return ErrorCode.None;
        }

        public ErrorCode Set(ArraySegment<PropEntry> newProps) {
            ErrorCode result = ErrorCode.None;
            int indxLimit = newProps.Offset + newProps.Count;
            for (int indx = newProps.Offset; indx < indxLimit; indx++) {
                PropEntry newProp = newProps.Array[indx];
                if (newProp.Value != null) {
                    result = CheckLockForUpdate(ref props[newProp.Index], newProp.LockId);
                    if (result == ErrorCode.None)
                        props[newProp.Index].Value = newProp.Value;
                    else
                        return result;
                }
                else {  // no value passed, so we just check the locks
                    result = CheckLockForClear(ref props[newProp.Index], newProp.LockId);
                    if (result != ErrorCode.None)
                        return result;
                }
                // everything checks out, so let's clear the lock
                props[newProp.Index].Lock = new PropLock();
            }
            return result;
        }

        public void SetDeleted() {
            key = null;
        }

        public bool IsDeleted
        {
            get { return key == null; }
        }

        #region Nested Types

        struct PropLock
        {
            int id;
            public int Id
            {
                get { return id; }
            }

            LockMode mode;
            public LockMode Mode
            {
                get { return mode; }
            }

            int timeStamp;
            public int TimeStamp
            {
                get { return timeStamp; }
            }

            public static PropLock StartLock(int id, LockMode mode) {
                PropLock result;
                result.id = id;
                result.mode = mode;
                result.timeStamp = Environment.TickCount;
                return result;
            }

            public bool IsOpen
            {
                get { return mode == LockMode.None; }
            }

            public bool IsExpired(TimeSpan lifeSpan) {
                TimeSpan elapsed = new TimeSpan(0, 0, 0, 0, (int)(Environment.TickCount - timeStamp));
                return elapsed > lifeSpan;
            }

            public override int GetHashCode() {
                return id ^ timeStamp ^ mode.GetHashCode();
            }

            public override bool Equals(object obj) {
                return obj is PropLock && this == (PropLock)obj;
            }

            public static bool operator ==(PropLock x, PropLock y) {
                return x.id == y.id && x.mode == y.mode && x.timeStamp == y.timeStamp;
            }

            public static bool operator !=(PropLock x, PropLock y) {
                return !(x == y);
            }
        }

        struct Prop
        {
            PropLock lck;

            public PropLock Lock
            {
                get { return lck; }
                internal set { lck = value; }
            }

            byte[] val;
            public byte[] Value
            {
                get
                {
                    if (object.ReferenceEquals(val, emptyValue))
                        return null;
                    else
                        return val;
                }
                internal set
                {
                    if (value == null)
                        val = emptyValue;
                    else
                        val = value;
                }
            }

            static byte[] emptyValue = new byte[0];

            public Prop(PropLock lck, byte[] value) {
                this.lck = lck;
                if (value == null)
                    val = emptyValue;
                else
                    val = value;
            }

            public static Prop CreateProp(int lockId, LockMode lockMode) {
                Prop result;
                result.lck = PropLock.StartLock(lockId, lockMode);
                result.val = emptyValue;
                return result;
            }

            // this is the only way to check if the property is "null" (not assigned)
            public bool IsEmpty
            {
                get { return val == null; }
            }

            // this sets the property back to "null"
            internal void ClearValue() {
                val = null;
            }
        }

        #endregion
    }
}
