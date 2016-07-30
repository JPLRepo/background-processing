using UnityEngine;
using System.Collections.Generic;

namespace BackgroundProcessing
{
    using DebugLevel = AddonConfig.DebugLevel;

    abstract class ResourceModuleHandler
    {
        public abstract HashSet<ProtoPartResourceSnapshot> HandleResource(Vessel v, VesselData data, HashSet<ProtoPartResourceSnapshot> modified);

        public static HashSet<ProtoPartResourceSnapshot> AddResource(VesselData data, float amount, string name, HashSet<ProtoPartResourceSnapshot> modified)
        {
            BackgroundProcessing.Debug("AddResource called, adding " + amount + " " + name, DebugLevel.ALL);

            if (!data.storage.ContainsKey(name)) { return modified; }

            bool reduce = amount < 0;

            List<ProtoPartResourceSnapshot> relevantStorage = data.storage[name];
            for (int i = 0; i < relevantStorage.Count; ++i)
            {
                ProtoPartResourceSnapshot r = relevantStorage[i];

                if (amount == 0) { break; }
                float n; float m;

                if (
                    float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
                    float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
                )
                {
                    n += amount; amount = 0;

                    if (reduce) { if (n < 0 && i < relevantStorage.Count - 1) { amount = n; n = 0; } }
                    else { if (n > m && i < relevantStorage.Count - 1) { amount = n - m; n = m; } }

                    r.resourceValues.SetValue("amount", n.ToString());
                }

                modified.Add(r);
            }

            return modified;
        }

        public static float ClampResource(HashSet<ProtoPartResourceSnapshot> modified)
        {
            float ret = 0;
            foreach (ProtoPartResourceSnapshot r in modified)
            {
                float n; float m; float i;

                if (
                    float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
                    float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
                )
                {
                    i = Mathf.Clamp(n, 0, m);
                    ret += i - n;
                    n = i;

                    r.resourceValues.SetValue("amount", n.ToString());
                }
            }

            return ret;
        }
    };
}