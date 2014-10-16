using System.Collections.Generic;
using System;
using UnityEngine;

namespace BackgroundProcessing {
	[KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Addon : MonoBehaviour {
		private Dictionary<Guid, HashSet<Callback<Vessel>>> backgroundCallbacks = new Dictionary<Guid, HashSet<Callback<Vessel>>>();

		static public Addon Instance { get; private set; }

		public Addon() {
			Instance = this;
		}

		public void Start() {
			DontDestroyOnLoad(this);
		}

		static public void RegisterCallback(Guid vesselId, Callback<Vessel> backgroundCallback) {
			Debug.Log("BackgroundProcessing: Callback registered");

			if (!Instance.backgroundCallbacks.ContainsKey(vesselId)) { Instance.backgroundCallbacks.Add(vesselId, new HashSet<Callback<Vessel>>()); }
			Instance.backgroundCallbacks[vesselId].Add(backgroundCallback);
		}

		static public void UnregisterCallback(Guid vesselId, Callback<Vessel> backgroundCallback) {
			if (Instance.backgroundCallbacks.ContainsKey(vesselId)) {
				Instance.backgroundCallbacks[vesselId].Remove(backgroundCallback);
			}
		}

		public void FixedUpdate() {
			if (FlightGlobals.fetch != null) {
				List<Vessel> vessels = new List<Vessel>(FlightGlobals.Vessels);

				foreach (Vessel v in vessels) {
					if (v.packed) {
						if (backgroundCallbacks.ContainsKey(v.id)) {
							foreach (Callback<Vessel> c in backgroundCallbacks[v.id]) {
								c.Invoke(v);
							}
						}
					}
				}
			}
		}
    }
}
