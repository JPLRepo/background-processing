using System.Collections.Generic;
using System;
using UnityEngine;

namespace BackgroundProcessing {
	[KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Addon : MonoBehaviour {

		public void RegisterHandler(String moduleName, IBackgroundHandler handler) {
			if (moduleHandlers.ContainsKey(moduleName)) { moduleHandlers[moduleName] = handler; }
			else { moduleHandlers.Add(moduleName, handler); }
		}

		static public Addon Instance { get; private set; }
		private Dictionary<Vessel, VesselData> vesselData = new Dictionary<Vessel, VesselData>();
		private Dictionary<String, IBackgroundHandler> moduleHandlers = new Dictionary<String, IBackgroundHandler>();

		// Need to load into this somehow (config file)
		// Also module name isn't sufficient to identify resource use (ModuleGenerator). Part name? Seems inelegant, doesn't scale.
		// Special case ModuleGenerator? What if there are others? Are there others? Also inelegant.
		private Dictionary<String, ResourceModuleData> resourceData = new Dictionary<String, ResourceModuleData>();

		private struct CallbackPair {
			public String moduleName;
			public uint partFlightID;

			public CallbackPair(String m, uint i) {
				moduleName = m;
				partFlightID = i;
			}
		};

		private class ResourceModuleData {
			public String resourceName = "";
			public float resourceAmount = 0;
		}

		private class VesselData {
			public List<CallbackPair> callbacks = new List<CallbackPair>();
			public List<ResourceModuleData> resourceModules = new List<ResourceModuleData>();
		}

		private VesselData GetVesselData(Vessel v)
		{
			VesselData ret = new VesselData();

			foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots) {
				foreach (ProtoPartModuleSnapshot m in p.modules) {
					if (moduleHandlers.ContainsKey(m.moduleName)) {
						ret.callbacks.Add(new CallbackPair(m.moduleName, p.flightID));
					}

					if (resourceData.ContainsKey(m.moduleName)) { ret.resourceModules.Add(resourceData[m.moduleName]); }
				}
			}

			Debug.Log("BackgroundProcessing: Found " + ret.callbacks.Count + " background modules on vessel " + v.name);
			Debug.Log("BackgroundProcessing: Found " + ret.resourceModules.Count + " resource modules on vessel " + v.name);

			return ret;
		}

		private bool CanGetVesselData(Vessel v) {
			return v.protoVessel != null;
		}

		public Addon() {
			Instance = this;
		}

		public void Start() {
			DontDestroyOnLoad(this);

			ResourceModuleData d = new ResourceModuleData();
			d.resourceAmount = 0.75f * TimeWarp.fixedDeltaTime;
			d.resourceName = "ElectricCharge";

			resourceData.Add("ModuleGenerator", d);
		}

		private void HandleResources(Vessel v) {
			VesselData data = vesselData[v];

			if (v.protoVessel.protoPartSnapshots.Count <= 0 || data.resourceModules.Count <= 0) {return;}
			Debug.Log("BackgroundProcessing: HandleResources on " + v.vesselName);

			Dictionary<String, List<ProtoPartResourceSnapshot>> storage = new Dictionary<String, List<ProtoPartResourceSnapshot>>();

			// Save this in the vesseldata structure, maybe. It can change if loaded and unpacked, though, so have to invalidate
			// on unpack/revalidate on pack.
			foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots) {
				foreach (ProtoPartResourceSnapshot r in p.resources) {
					if (!storage.ContainsKey(r.resourceName)) { storage.Add(r.resourceName, new List<ProtoPartResourceSnapshot>()); }
					storage[r.resourceName].Add(r);
				}
			}

			HashSet<ProtoPartResourceSnapshot> modified = new HashSet<ProtoPartResourceSnapshot>();

			foreach (ResourceModuleData d in data.resourceModules) {
				if (!storage.ContainsKey(d.resourceName)) {continue;}

				float amount = d.resourceAmount * TimeWarp.CurrentRate;
				List<ProtoPartResourceSnapshot> relevantStorage = storage[d.resourceName];
				for (int i = 0; i < relevantStorage.Count; ++i) {
					ProtoPartResourceSnapshot r = relevantStorage[i];

					if (amount == 0) {break;}

					float n;
					float m;

					Debug.Log("BackgroundProcessing: Parsing out resource values");

					if (
						float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
						float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
					) {
						Debug.Log("BackgroundProcessing: Adding " + amount + " " + d.resourceName + " to storage");
						Debug.Log("BackgroundProcessing: Storage currently contains " + n + "/" + m);
						n += amount;
						amount = 0;

						if (amount < 0) {
							if (n < 0 && i < relevantStorage.Count - 1) {
								amount += n;
								n = 0;
							}
						}
						else {
							if (n > m && i < relevantStorage.Count - 1) {
								amount += n - m;
								n = m;
							}
						}

						r.resourceValues.SetValue("amount", n.ToString());
						Debug.Log("BackgroundProcessing: Storage afterwards contains " + n + "/" + m);
					}

					modified.Add(r);
				};
			}

			foreach (ProtoPartResourceSnapshot r in modified) {
				float n;
				float m;

				if (
					float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
					float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
				) {
					n = Mathf.Clamp(n, 0, m);
					Debug.Log("BackgroundProcessing: Setting storage to " + n + "/" + m);
					r.resourceValues.SetValue("amount", n.ToString());
				}
			}
		}

		/* Progression: Unloaded/packed -> loaded/packed -> loaded/unpacked -> active vessel
		 * While packed: No physics. Position of stuff is handled by orbital information
		 * While loaded: Parts exist, resources are handled.
		 * 
		 * Load at 2.5 km, unpack at 300 m (except for landed stuff)
		 */

		public void FixedUpdate() {
			if (FlightGlobals.fetch != null) {
				List<Vessel> vessels = new List<Vessel>(FlightGlobals.Vessels);
				vessels.Remove(FlightGlobals.ActiveVessel);

				foreach (Vessel v in vessels) {
					if (!vesselData.ContainsKey(v)) {
						if (!CanGetVesselData(v)) {continue;}

						vesselData.Add(v, GetVesselData(v));
					}

					if (v.situation != Vessel.Situations.PRELAUNCH) {
						if (!v.loaded) {HandleResources(v);}

						if (!v.loaded || v.packed) {
							foreach (CallbackPair p in vesselData[v].callbacks) {
								moduleHandlers[p.moduleName].FixedBackgroundUpdate(v, p.partFlightID);
							}
						}
					}
				}
			}
		}
    }
}
