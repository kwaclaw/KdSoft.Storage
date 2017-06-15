using System;

namespace KdSoft.Services.StorageServices
{
  /// <summary>
  /// Base class for store implementations.
  /// </summary>
  /// <typeparam name="S">Recursive type parameter for type safety in subclasses.</typeparam>
  public abstract class Store<S> where S : Store<S>
  {
    StorageManager<S> storeMgr;  // access must be serialized

    /// <summary>
    /// Storage manager that owns this store.
    /// </summary>
    public StorageManager<S> StoreMgr {
      get { return storeMgr; }
    }

    /// <summary>
    /// Indicates if store is closed.
    /// </summary>
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

    /// <inheritdoc/>
    public override int GetHashCode() {
      return Name.GetHashCode();
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) {
      return Name.Equals((obj as Store<S>)?.Name);
    }

    /// <summary>
    /// Closes store, performing cleanup operations.
    /// </summary>
    protected internal abstract void Close();

    /// <summary>
    /// Removes this store from its storage manager.
    /// </summary>
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
