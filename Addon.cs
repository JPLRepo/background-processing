using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;

namespace BackgroundProcessing {
	using ResourceRequestFunc = Func<Vessel, float, string, float>;
	using BackgroundUpdateResourceFunc = Action<Vessel, uint, Func<Vessel, float, string, float>>;
	using BackgroundUpdateFunc = Action<Vessel, uint>;

	[KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class Addon : MonoBehaviour {
		public float RequestBackgroundResource(Vessel vessel, float amount, string resource) {
			if (!vesselData.ContainsKey(vessel)) { return 0f; }

			HashSet<ProtoPartResourceSnapshot> modified = new HashSet<ProtoPartResourceSnapshot>();

			AddResource(vesselData[vessel], -amount, resource, modified);
			float ret = ClampResource(modified);

			return amount - ret;
		}

		static public Addon Instance { get; private set; }

		class UpdateHelper {
			private BackgroundUpdateResourceFunc resourceFunc = null;
			private BackgroundUpdateFunc updateFunc = null;

			public UpdateHelper(BackgroundUpdateResourceFunc rf) {
				resourceFunc = rf;
			}

			public UpdateHelper(BackgroundUpdateFunc f) {
				updateFunc = f;
			}

			public void Invoke(Vessel v, uint id, ResourceRequestFunc r) {
				if (resourceFunc == null) { updateFunc.Invoke(v, id); }
				else { resourceFunc.Invoke(v, id, r); }
			}
		}

		private Dictionary<Vessel, VesselData> vesselData = new Dictionary<Vessel, VesselData>();
		private Dictionary<string, UpdateHelper> moduleHandlers = new Dictionary<string, UpdateHelper>();
		private Dictionary<string, List<ResourceModuleData>> resourceData = new Dictionary<string, List<ResourceModuleData>>();
		private HashSet<string> interestingResources = new HashSet<string>();

		private class ResourceModuleData {
			public string resourceName { get; private set; }
			public float resourceRate { get; private set; }
			public FloatCurve powerCurve { get; private set; }

			public ResourceModuleData(string rn = "", float rr = 0, FloatCurve pc = null) {
				resourceName = rn;
				resourceRate = rr;
				powerCurve = pc;
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
			public Dictionary<string, List<ProtoPartResourceSnapshot>> storage = new Dictionary<string, List<ProtoPartResourceSnapshot>>();
		}

		private bool HasResourceGenerationData(PartModule m, ProtoPartModuleSnapshot s) {
			if (m.moduleName == "ModuleDeployableSolarPanel") {
				if (s.moduleValues.GetValue("stateString") == ModuleDeployableSolarPanel.panelStates.EXTENDED.ToString()) {
					return true;
				}
			}
			if (m.moduleName == "ModuleCommand") {
				ModuleCommand c = (ModuleCommand)m;

				foreach (ModuleResource mr in c.inputResources) {
					if (interestingResources.Contains(mr.name)) { return true; }
				}
			}

			if (m.moduleName == "ModuleGenerator") {
				if (s.moduleValues.GetValue("generatorIsActive") == "true") {
					ModuleGenerator g = (ModuleGenerator)m;
					if (g.inputList.Count <= 0) {
						foreach (ModuleGenerator.GeneratorResource gr in g.outputList) {
							if (interestingResources.Contains(gr.name)) { return true; }
						}
					}
				}
			}

			return resourceData.ContainsKey(m.moduleName);
		}

		private List<ResourceModuleData> GetResourceGenerationData(PartModule m, ProtoPartModuleSnapshot s) {
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
					ret.Add(new ResourceModuleData(p.resourceName, p.chargeRate, p.powerCurve));
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
				PartModuleList partModuleList = PartLoader.getPartInfoByName(p.partName).partPrefab.Modules;
				if (partModuleList.Count == p.modules.Count) {
					for (int i = 0; i < partModuleList.Count; ++i) {
						if (partModuleList[i].moduleName == p.modules[i].moduleName) {
							if (moduleHandlers.ContainsKey(partModuleList[i].moduleName)) {
								ret.callbacks.Add(new CallbackPair(partModuleList[i].moduleName, p.flightID));
							}

							if (HasResourceGenerationData(partModuleList[i], p.modules[i])) { ret.resourceModules.AddRange(GetResourceGenerationData(partModuleList[i], p.modules[i])); }
						}
						else {Debug.LogError("BackgroundProcessing: PartModule/ProtoPartModuleSnapshot sync error processing part " + p.partName + ". Something is very wrong.");}
					}
				}

				/*
				// Figure out enabled/disabled status. Same ordering as ProtoPartModuleSnapshot list?
				foreach (PartModule m in PartLoader.getPartInfoByName(p.partName).partPrefab.Modules) {
					if (moduleHandlers.ContainsKey(m.moduleName)) {
						ret.callbacks.Add(new CallbackPair(m.moduleName, p.flightID));
					}

					if (HasResourceGenerationData(m)) { ret.resourceModules.AddRange(GetResourceGenerationData(m)); }
				}*/

				foreach (ProtoPartResourceSnapshot r in p.resources) {
					if (!ret.storage.ContainsKey(r.resourceName)) { ret.storage.Add(r.resourceName, new List<ProtoPartResourceSnapshot>()); }
					ret.storage[r.resourceName].Add(r);
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
						MethodInfo fbu = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[2] { typeof(Vessel), typeof(uint)}, null);
						MethodInfo fbur = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(ResourceRequestFunc) }, null);
						if (fbur != null) { moduleHandlers.Add(m.moduleName, new UpdateHelper((BackgroundUpdateResourceFunc)Delegate.CreateDelegate(typeof(BackgroundUpdateResourceFunc), fbur))); }
						else {
							if (fbu != null) { moduleHandlers.Add(m.moduleName, new UpdateHelper((BackgroundUpdateFunc)Delegate.CreateDelegate(typeof(BackgroundUpdateFunc), fbu))); }
						}

						
						

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

		private HashSet<ProtoPartResourceSnapshot> AddResource(VesselData data, float amount, string name, HashSet<ProtoPartResourceSnapshot> modified) {
			if (!data.storage.ContainsKey(name)) { return modified; }

			bool reduce = amount < 0;

			List<ProtoPartResourceSnapshot> relevantStorage = data.storage[name];
			for (int i = 0; i < relevantStorage.Count; ++i) {
				ProtoPartResourceSnapshot r = relevantStorage[i];

				if (amount == 0) { break; }
				float n; float m;

				if (
					float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
					float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
				) {
					n += amount; amount = 0;

					if (reduce) {if (n < 0 && i < relevantStorage.Count - 1) { amount = n; n = 0; }}
					else { if (n > m && i < relevantStorage.Count - 1) { amount = n - m; n = m; } }

					r.resourceValues.SetValue("amount", n.ToString());
				}

				modified.Add(r);
			}

			return modified;
		}

		private float ClampResource(HashSet<ProtoPartResourceSnapshot> modified) {
			float ret = 0;
			foreach (ProtoPartResourceSnapshot r in modified) {
				float n; float m; float i;

				if (
					float.TryParse(r.resourceValues.GetValue("amount"), out n) &&
					float.TryParse(r.resourceValues.GetValue("maxAmount"), out m)
				) {
					i = Mathf.Clamp(n, 0, m);
					ret += i - n;
					n = i;

					r.resourceValues.SetValue("amount", n.ToString());
				}
			}

			return ret;
		}

		private void HandleResources(Vessel v) {
			VesselData data = vesselData[v];
			if (v.protoVessel.protoPartSnapshots.Count <= 0 || data.resourceModules.Count <= 0) {return;}

			HashSet<ProtoPartResourceSnapshot> modified = new HashSet<ProtoPartResourceSnapshot>();

			foreach (ResourceModuleData d in data.resourceModules) {
				if (d.powerCurve == null) {
					AddResource(data, d.resourceRate * TimeWarp.CurrentRate * TimeWarp.fixedDeltaTime, d.resourceName, modified);
				}
				else {
					CelestialBody kerbol = FlightGlobals.Bodies[0];
					RaycastHit hitInfo;
					bool hit = Physics.Raycast(v.GetWorldPos3D(), kerbol.position - v.GetWorldPos3D(), out hitInfo);

					if (!hit || hitInfo.collider.gameObject == kerbol) {
						AddResource(data, d.resourceRate * TimeWarp.CurrentRate * TimeWarp.fixedDeltaTime * d.powerCurve.Evaluate((float)kerbol.GetAltitude(v.GetWorldPos3D())), d.resourceName, modified);
					}
				}
			}

			ClampResource(modified);
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
								moduleHandlers[p.moduleName].Invoke(v, p.partFlightID, RequestBackgroundResource);
							}
						}
						else {vesselData.Remove(v);}
					}
				}
			}
		}
    }
}
