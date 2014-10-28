using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;

namespace BackgroundProcessing {
	[KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class Addon : MonoBehaviour {
		public delegate void BackgroundUpdate(Vessel v, uint partFlightID);

		static public Addon Instance { get; private set; }

		private Dictionary<Vessel, VesselData> vesselData = new Dictionary<Vessel, VesselData>();
		private Dictionary<string, BackgroundUpdate> moduleHandlers = new Dictionary<string, BackgroundUpdate>();
		private Dictionary<string, List<ResourceModuleData>> resourceData = new Dictionary<string, List<ResourceModuleData>>();
		private HashSet<string> interestingResources = new HashSet<string>();

		private class ResourceModuleData {
			public string resourceName { get; private set; }
			public float resourceRate { get; private set; }

			public ResourceModuleData(string rn = "", float rr = 0) {
				resourceName = rn;
				resourceRate = rr;
			}
		}

		private struct CallbackPair {
			public string moduleName;
			public uint partFlightID;

			public CallbackPair(string m, uint i) {
				moduleName = m;
				partFlightID = i;
			}
		};

		private class VesselData {
			public List<CallbackPair> callbacks = new List<CallbackPair>();
			public List<ResourceModuleData> resourceModules = new List<ResourceModuleData>();
		}

		private bool HasResourceGenerationData(PartModule m) {
			if (m.moduleName == "ModuleDeployableSolarPanel") { return true; }
			if (m.moduleName == "ModuleCommand") {
				ModuleCommand c = (ModuleCommand)m;

				foreach (ModuleResource mr in c.inputResources) {
					if (interestingResources.Contains(mr.name)) { return true; }
				}
			}

			if (m.moduleName == "ModuleGenerator") {
				ModuleGenerator g = (ModuleGenerator)m;
				if (g.inputList.Count <= 0) {
					foreach (ModuleGenerator.GeneratorResource gr in g.outputList) {
						if (interestingResources.Contains(gr.name)) { return true; }
					}
				}
			}

			return resourceData.ContainsKey(m.moduleName);
		}

		private List<ResourceModuleData> GetResourceGenerationData(PartModule m) {
			List<ResourceModuleData> ret = new List<ResourceModuleData>();

			if (m.moduleName == "ModuleGenerator") {
				ModuleGenerator g = (ModuleGenerator)m;

				if (g.inputList.Count <= 0) {
					foreach (ModuleGenerator.GeneratorResource gr in g.outputList) {
						if (interestingResources.Contains(gr.name)) {
							ret.Add(new ResourceModuleData(gr.name, gr.rate));
						}
					}
				}
			}

			if (m.moduleName == "ModuleDeployableSolarPanel") {
				// Figure out power curve later.
				ModuleDeployableSolarPanel p = (ModuleDeployableSolarPanel)m;
				if (interestingResources.Contains(p.resourceName)) {
					ret.Add(new ResourceModuleData(p.resourceName, p.chargeRate));
				}
			}

			if (m.moduleName == "ModuleCommand") {
				ModuleCommand c = (ModuleCommand)m;
				foreach (ModuleResource mr in c.inputResources) {
					if (interestingResources.Contains(mr.name)) {
						ret.Add(new ResourceModuleData(mr.name, (float)-mr.rate));
					}
				}
			}

			if (resourceData.ContainsKey(m.moduleName)) { ret.AddRange(resourceData[m.moduleName]); }
			return ret;
		}

		private VesselData GetVesselData(Vessel v)
		{
			VesselData ret = new VesselData();

			foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots) {
				// Figure out enabled/disabled status. Same ordering as ProtoPartModuleSnapshot list?
				foreach (PartModule m in PartLoader.getPartInfoByName(p.partName).partPrefab.Modules) {
					if (moduleHandlers.ContainsKey(m.moduleName)) {
						ret.callbacks.Add(new CallbackPair(m.moduleName, p.flightID));
					}

					if (HasResourceGenerationData(m)) { ret.resourceModules.AddRange(GetResourceGenerationData(m)); }
				}
			}

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

			interestingResources.Add("ElectricCharge");

			foreach (AvailablePart a in PartLoader.LoadedPartsList) {
				if (a.partPrefab.Modules != null) {
					foreach (PartModule m in a.partPrefab.Modules) {
						MethodInfo fbu = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[2] { typeof(Vessel), typeof(uint) }, null);
						if (fbu != null) { moduleHandlers.Add(m.moduleName, (BackgroundUpdate)Delegate.CreateDelegate(typeof(BackgroundUpdate), fbu)); }

						MethodInfo ir = m.GetType().GetMethod("GetInterestingResources", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
						if (ir != null && ir.ReturnType == typeof(List<string>)) {
							interestingResources.UnionWith((List<String>)ir.Invoke(null, null));
						}

						MethodInfo prc = m.GetType().GetMethod("GetBackgroundResourceCount", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
						MethodInfo pr = m.GetType().GetMethod("GetBackgroundResource", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(int), typeof(string).MakeByRefType(), typeof(float).MakeByRefType() }, null);
						if (prc != null && pr != null && prc.ReturnType == typeof(int)) {
							System.Object[] resourceParams = new System.Object[3];
							resourceParams[1] = "";
							resourceParams[2] = 0.0f;

							for (int count = (int)prc.Invoke(null, null); count > 0; --count) {
								resourceParams[0] = count;
								pr.Invoke(null, resourceParams);

								if (!resourceData.ContainsKey(m.moduleName)) { resourceData.Add(m.moduleName, new List<ResourceModuleData>()); }
								resourceData[m.moduleName].Add(new ResourceModuleData((string)resourceParams[1], (float)resourceParams[2]));
							}
						}
					}
				}
			}
		}

		private void HandleResources(Vessel v) {
			VesselData data = vesselData[v];
			if (v.protoVessel.protoPartSnapshots.Count <= 0 || data.resourceModules.Count <= 0) {return;}

			Dictionary<string, List<ProtoPartResourceSnapshot>> storage = new Dictionary<string, List<ProtoPartResourceSnapshot>>();

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

				float amount = d.resourceRate * TimeWarp.CurrentRate * TimeWarp.fixedDeltaTime;
				List<ProtoPartResourceSnapshot> relevantStorage = storage[d.resourceName];
				for (int i = 0; i < relevantStorage.Count; ++i) {
					ProtoPartResourceSnapshot r = relevantStorage[i];

					if (amount == 0) {break;}
					float n; float m;

					if (
						float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
						float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
					) {
						n += amount;

						if (amount < 0) { if (n < 0 && i < relevantStorage.Count - 1) {amount = n; n = 0; } }
						else { if (n > m && i < relevantStorage.Count - 1) {amount = n - m; n = m;} }

						r.resourceValues.SetValue("amount", n.ToString());
					}

					modified.Add(r);
				};
			}

			foreach (ProtoPartResourceSnapshot r in modified) {
				float n; float m;

				if (
					float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
					float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
				) {
					n = Mathf.Clamp(n, 0, m);
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
					if (v.situation != Vessel.Situations.PRELAUNCH) {
						if (!v.loaded || v.packed) {
							if (!vesselData.ContainsKey(v)) {
								if (!CanGetVesselData(v)) { continue; }

								vesselData.Add(v, GetVesselData(v));
							}

							if (!v.loaded) { HandleResources(v); }

							foreach (CallbackPair p in vesselData[v].callbacks) {
								moduleHandlers[p.moduleName].Invoke(v, p.partFlightID);
							}
						}
						else {vesselData.Remove(v);}
					}
				}
			}
		}
    }
}
