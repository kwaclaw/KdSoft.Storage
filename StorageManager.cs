using System.Collections.Generic;

namespace KdSoft.Services.StorageServices
{
    /// <summary>
    /// This class is responsible for managing <see cref="Store{T, R}"/> instances,
    /// acting as an intermediary between them and the <see cref="StorageRuntime"/>.
    /// </summary>
    /// <typeparam name="R">Type of storage runtime.</typeparam>
    /// <typeparam name="T">Type of  store implementation.</typeparam>
    public class StorageManager<S> where S : Store<S>
    {
        protected readonly object storeLock = new object();
        protected readonly Dictionary<string, S> stores;

        public StorageManager() {
            stores = new Dictionary<string, S>();
        }

        internal bool RemoveStore(S store) {
            bool result = false;
            lock (storeLock) {
                result = stores.Remove(store.Name);
            }
            return result;
        }

        internal void AddStore(string name, S store) {
            lock (storeLock) {
                stores.Add(name, store);
            }
        }

        public void CloseStores() {
            lock (storeLock) {
                foreach (KeyValuePair<string, S> entry in stores)
                    entry.Value.Close();
                stores.Clear();
            }
        }

        #region StorageAdmin and StorageProvider Related

        public S GetStore(string name) {
            S store;
            lock (storeLock) {
                if (!stores.TryGetValue(name, out store))
                    store = null;
            }
            return store;
        }

        public string[] ListStores() {
            string[] result;
            lock (storeLock) {
                result = new string[stores.Count];
                int indx = 0;
                foreach (KeyValuePair<string, S> entry in stores)
                    result[indx++] = entry.Value.Name;
            }
            return result;
        }

        #endregion
    }
}
