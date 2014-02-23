using System;

namespace KdSoft.Services.StorageServices
{
  /// <summary>
  /// Base class for store implementations.
  /// </summary>
  /// <typeparam name="S">Recursive type parameter for type safety in subclasses.</typeparam>
  public abstract class Store<S> where S: Store<S>
  {
    StorageManager<S> storeMgr;  // access must be serialized

    public StorageManager<S> StoreMgr {
      get { return storeMgr; }
    }

    public bool IsClosed {
      get { return storeMgr == null; }
    }

    public readonly object SyncRoot;

    public readonly string Name;
    public readonly PropDesc[] PropDescs;

    public Store(StorageManager<S> storeMgr, string name, PropDesc[] propDescs) {
      if (storeMgr == null)
        throw new ArgumentNullException("storeMgr");
      SyncRoot = new object();
      this.storeMgr = storeMgr;
      this.Name = name;
      this.PropDescs = propDescs;
      storeMgr.AddStore(name, (S)this);
    }

    public override int GetHashCode() {
      return Name.GetHashCode();
    }

    // cleans up
    protected internal abstract void Close();

    public void Remove() {
      lock (SyncRoot) {
        StorageManager<S> sm = storeMgr;
        if (sm != null) {
          sm.RemoveStore((S)this);
          storeMgr = null;
          Close();
        }
      }
    }
  }
}
