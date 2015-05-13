using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;
using System.Linq;

namespace BackgroundProcessing {
	using ResourceRequestFunc = Func<Vessel, float, string, float>;
	public delegate void BackgroundUpdateResourceFunc(Vessel v, uint partFlightId, ResourceRequestFunc resourceFunc, ref System.Object data);
	public delegate void BackgroundUpdateFunc(Vessel v, uint partFlightId, ref System.Object data);
	
	public delegate void BackgroundSaveFunc(Vessel v, uint partFlightId, System.Object data);
	public delegate void BackgroundLoadFunc(Vessel v, uint partFlightId, ref System.Object data);

	[KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class Addon : MonoBehaviour {
		public float RequestBackgroundResource(Vessel vessel, float amount, string resource) {
			if (!vesselData.ContainsKey(vessel)) { return 0f; }

			HashSet<ProtoPartResourceSnapshot> modified = new HashSet<ProtoPartResourceSnapshot>();

			AddResource(vesselData[vessel], -amount, resource, modified);
			float ret = ClampResource(modified);

			return amount - ret;
		}

		static private bool loaded = false;
		static private Addon mInstance = null;
		public Addon Instance {
			get { return mInstance; }
			private set { mInstance = value; }
		}

		class UpdateHelper {
			private BackgroundUpdateResourceFunc resourceFunc = null;
			private BackgroundUpdateFunc updateFunc = null;

			private BackgroundLoadFunc loadFunc = null;
			private BackgroundSaveFunc saveFunc = null;

			public UpdateHelper(BackgroundUpdateResourceFunc rf, BackgroundLoadFunc lf, BackgroundSaveFunc sf) {
				resourceFunc = rf;
				loadFunc = lf;
				saveFunc = sf;
			}

			public UpdateHelper(BackgroundUpdateFunc f, BackgroundLoadFunc lf, BackgroundSaveFunc sf) {
				updateFunc = f;
				loadFunc = lf;
				saveFunc = sf;
			}

			public void Invoke(Vessel v, uint id, ResourceRequestFunc r, ref System.Object data) {
				if (resourceFunc == null) { updateFunc.Invoke(v, id, ref data); }
				else { resourceFunc.Invoke(v, id, r, ref data); }
			}

			public void Load(Vessel v, uint id, ref System.Object data) {
				if (loadFunc != null) {loadFunc.Invoke(v, id, ref data);}
			}

			public void Save(Vessel v, uint id, System.Object data) {
				if (saveFunc != null) { saveFunc.Invoke(v, id, data); }
			}
		}

		private Dictionary<Vessel, VesselData> vesselData = new Dictionary<Vessel, VesselData>();
		private Dictionary<string, UpdateHelper> moduleHandlers = new Dictionary<string, UpdateHelper>();
		private Dictionary<string, List<ResourceModuleData>> resourceData = new Dictionary<string, List<ResourceModuleData>>();
		private HashSet<string> interestingResources = new HashSet<string>();

		private class SolarPanelData {
			public FloatCurve powerCurve { get; private set; }
			public Vector3d position { get; private set; }
			public Quaternion orientation { get; private set; }
			public Vector3d solarNormal {get; private set;}
			public Vector3d pivotAxis { get; private set; }
			public bool tracks { get; private set; }
			public bool usesCurve { get; private set; }
			public FloatCurve tempCurve {get; private set;}
			public float temperature { get; private set; }

			public SolarPanelData(FloatCurve pc, Vector3d p, Quaternion o, Vector3d sn, Vector3d pa, bool t, bool uC, FloatCurve tE, float tmp) {
				powerCurve = pc;
				position = p;
				orientation = o;
				solarNormal = sn;
				pivotAxis = pa;
				tracks = t;
				usesCurve = uC;
				tempCurve = tE;
				temperature = tmp;
			}
		}

		private class ResourceModuleData {
			public string resourceName { get; private set; }
			public float resourceRate { get; private set; }
			public SolarPanelData panelData {get; private set;}

			public ResourceModuleData(string rn = "", float rr = 0, SolarPanelData panel = null) {				
				resourceName = rn;
				resourceRate = rr;
				panelData = panel;
			}
		}

		private struct CallbackPair : IEquatable<CallbackPair> {
			public string moduleName;
			public uint partFlightID;

			public CallbackPair(string m, uint i) {
				moduleName = m;
				partFlightID = i;
			}

			public override int GetHashCode() {
				return moduleName.GetHashCode() + partFlightID.GetHashCode();
			}

			public bool Equals(CallbackPair rhs) {
				return moduleName == rhs.moduleName && partFlightID == rhs.partFlightID;
			}
		};

		class ObjectHolder {
			public System.Object data;
		};

		private class VesselData {
			public Dictionary<CallbackPair, ObjectHolder> callbacks = new Dictionary<CallbackPair, ObjectHolder>();
			public List<ResourceModuleData> resourceModules = new List<ResourceModuleData>();
			public Dictionary<string, List<ProtoPartResourceSnapshot>> storage = new Dictionary<string, List<ProtoPartResourceSnapshot>>();
		}

		private bool HasResourceGenerationData(PartModule m, ProtoPartModuleSnapshot s) {
			if (m != null && m.moduleName == "ModuleDeployableSolarPanel") {
				if (s.moduleValues.GetValue("stateString") == ModuleDeployableSolarPanel.panelStates.EXTENDED.ToString()) {
					return true;
				}
			}

			if (m != null && m.moduleName == "ModuleCommand") {
				ModuleCommand c = (ModuleCommand)m;

				foreach (ModuleResource mr in c.inputResources) {
					if (interestingResources.Contains(mr.name)) { return true; }
				}
			}

			if (m != null && m.moduleName == "ModuleGenerator") {
				bool active = false;
				Boolean.TryParse(s.moduleValues.GetValue("generatorIsActive"), out active);
				if (active) {
					ModuleGenerator g = (ModuleGenerator)m;
					if (g.inputList.Count <= 0) {
						foreach (ModuleGenerator.GeneratorResource gr in g.outputList) {
							if (interestingResources.Contains(gr.name)) {return true; }
						}
					}
				}
			}

			return resourceData.ContainsKey(s.moduleName);
		}

		private List<ResourceModuleData> GetResourceGenerationData(PartModule m, ProtoPartSnapshot part) {
			List<ResourceModuleData> ret = new List<ResourceModuleData>();

			if (m != null && m.moduleName == "ModuleGenerator") {
				ModuleGenerator g = (ModuleGenerator)m;

				if (g.inputList.Count <= 0) {
					foreach (ModuleGenerator.GeneratorResource gr in g.outputList) {
						if (interestingResources.Contains(gr.name)) {
							ret.Add(new ResourceModuleData(gr.name, gr.rate));
						}
					}
				}
			}

			if (m != null && m.moduleName == "ModuleDeployableSolarPanel") {
				ModuleDeployableSolarPanel p = (ModuleDeployableSolarPanel)m;

				if (interestingResources.Contains(p.resourceName)) {
					Transform panel = p.part.FindModelComponent<Transform>(p.raycastTransformName);
					Transform pivot = p.part.FindModelComponent<Transform>(p.pivotName);

					ret.Add(new ResourceModuleData(p.resourceName, p.chargeRate, new SolarPanelData(p.powerCurve, part.position, part.rotation, panel.forward, pivot.up, p.sunTracking, p.useCurve, p.temperatureEfficCurve, (float)part.temperature)));
				}
			}

			if (m != null && m.moduleName == "ModuleCommand") {
				ModuleCommand c = (ModuleCommand)m;
				foreach (ModuleResource mr in c.inputResources) {
					if (interestingResources.Contains(mr.name)) {
						ret.Add(new ResourceModuleData(mr.name, (float)-mr.rate));
					}
				}
			}

			if (m != null && resourceData.ContainsKey(m.moduleName)) { ret.AddRange(resourceData[m.moduleName]); }
			return ret;
		}

		private VesselData GetVesselData(Vessel v)
		{
			VesselData ret = new VesselData();

			if (v.protoVessel != null) {
				foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots) {
					Part part = PartLoader.getPartInfoByName(p.partName).partPrefab;

					if (part == null) {
						Debug.LogWarning("BackgroundProcessing: Couldn't find PartPrefab for part " + p.partName);
						continue;
					}

					if (part.Modules == null) { continue; }

					for (int i = 0; i < p.modules.Count; ++i) {
						if (p.modules[i].moduleName == null) {
							Debug.LogWarning("BackgroundProcessing: Null moduleName for module " + i + "/" + p.modules.Count);
							Debug.LogWarning("BackgroundProcessing: Module values: " + p.modules[i].moduleValues);
							continue;
						}

						if (moduleHandlers.ContainsKey(p.modules[i].moduleName)) {
							ret.callbacks.Add(new CallbackPair(p.modules[i].moduleName, p.flightID), new ObjectHolder());
						}

						int j = i;
						if (j >= part.Modules.Count || part.Modules[j].moduleName != p.modules[i].moduleName) {
							if (j < part.Modules.Count) {
								Debug.LogWarning("BackgroundProcessing: Expected " + p.modules[i].moduleName + " at index " + i + ", got " + part.Modules[j].moduleName);

								for (j = i; j < part.Modules.Count; ++j) {
									if (part.Modules[j].moduleName == p.modules[i].moduleName) {
										Debug.LogWarning("BackgroundProcessing: Found " + p.modules[i].moduleName + " at index " + j);
										break;
									}
								}
							}
						}

						if (j < part.Modules.Count) {
							if (HasResourceGenerationData(part.Modules[j], p.modules[i])) {
								ret.resourceModules.AddRange(GetResourceGenerationData(part.Modules[j], p));
							}
						}
						else {
							Debug.LogWarning("BackgroundProcessing: Ran out of modules before finding module " + p.modules[i].moduleName);

							if (HasResourceGenerationData(null, p.modules[i])) {
								ret.resourceModules.AddRange(GetResourceGenerationData(null, p));
							}
						}
					}

					foreach (ProtoPartResourceSnapshot r in p.resources) {
						if (r.resourceName == null) {
							Debug.LogWarning("BackgroundProcessing: Null resourceName.");
							Debug.LogWarning("BackgroundProcessing: Resource values: " + r.resourceValues);
							continue;
						}

						bool flowState;

						if (bool.TryParse(r.resourceValues.GetValue("flowState"), out flowState)) {
							if (!flowState) { continue; }
						}
						else {
							Debug.LogWarning("BackgroundProcessing: failed to read flow state for resource " + r.resourceName);
							Debug.LogWarning("BackgroundProcessing: Resource values: " + r.resourceValues);
						}

						if (!ret.storage.ContainsKey(r.resourceName)) { ret.storage.Add(r.resourceName, new List<ProtoPartResourceSnapshot>()); }
						ret.storage[r.resourceName].Add(r);
					}
				}
			}

			return ret;
		}

		private bool CanGetVesselData(Vessel v) {
			return v.protoVessel != null;
		}

		public bool IsMostRecentAssembly() {
			Assembly me = Assembly.GetExecutingAssembly();

			foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies) {
				if (la.assembly.GetName().Name != me.GetName().Name) { continue; }
			}

			foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies) {
				if (la.assembly.GetName().Name != me.GetName().Name) {continue;}

				if (la.assembly.GetName().Version > me.GetName().Version) { return false; }
			}

			return true;
		}

		public void Awake() {
			if (loaded || !IsMostRecentAssembly()) {
				Debug.Log("BackgroundProcessing: Assembly " + Assembly.GetExecutingAssembly().Location + " (" + Assembly.GetExecutingAssembly().GetName().Version + ") not running because:");
				if (loaded) { Debug.Log("BackgroundProcessing already loaded"); }
				else { Debug.Log("IsMostRecentAssembly returned false"); }
				Destroy(gameObject);
				return;
			}

			Debug.Log("BackgroundProcessing: Running assembly at " + Assembly.GetExecutingAssembly().Location + " (" + Assembly.GetExecutingAssembly().GetName().Version + ")");

			Instance = this;
			loaded = true;
			DontDestroyOnLoad(this);

			interestingResources.Add("ElectricCharge");

			HashSet<String> processed = new HashSet<String>();

			foreach (AvailablePart a in PartLoader.LoadedPartsList) {
				if (a.partPrefab.Modules != null) {
					foreach (PartModule m in a.partPrefab.Modules) {
						if (processed.Contains(m.moduleName)) { continue; }
						processed.Add(m.moduleName);

						MethodInfo lf = m.GetType().GetMethod("BackgroundLoad", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object).MakeByRefType() }, null);
						MethodInfo sf = m.GetType().GetMethod("BackgroundSave", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object) }, null);

						MethodInfo fbu = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object).MakeByRefType()}, null);
						MethodInfo fbur = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[4] { typeof(Vessel), typeof(uint), typeof(ResourceRequestFunc), typeof(System.Object).MakeByRefType() }, null);
						if (fbur != null) {
							moduleHandlers[m.moduleName] = new UpdateHelper(
								(BackgroundUpdateResourceFunc)Delegate.CreateDelegate(typeof(BackgroundUpdateResourceFunc), fbur),
								lf != null ? (BackgroundLoadFunc)Delegate.CreateDelegate(typeof(BackgroundLoadFunc), lf) : null,
								sf != null ? (BackgroundSaveFunc)Delegate.CreateDelegate(typeof(BackgroundSaveFunc), sf) : null
							);
						}
						else {
							if (fbu != null) {
								moduleHandlers[m.moduleName] = new UpdateHelper(
									(BackgroundUpdateFunc)Delegate.CreateDelegate(typeof(BackgroundUpdateFunc), fbu),
									lf != null ? (BackgroundLoadFunc)Delegate.CreateDelegate(typeof(BackgroundLoadFunc), lf) : null,
									sf != null ? (BackgroundSaveFunc)Delegate.CreateDelegate(typeof(BackgroundSaveFunc), sf) : null
								);
							}
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

							resourceData[m.moduleName] = new List<ResourceModuleData>();
							for (int count = (int)prc.Invoke(null, null); count > 0; --count) {
								resourceParams[0] = count;
								pr.Invoke(null, resourceParams);

								resourceData[m.moduleName].Add(new ResourceModuleData((string)resourceParams[1], (float)resourceParams[2]));
							}
						}
					}
				}
			}

			GameEvents.onLevelWasLoaded.Add(ClearVesselData);
			GameEvents.onGameStateSave.Add(OnSave);
		}

		private HashSet<ProtoPartResourceSnapshot> AddResource(VesselData data, float amount, string name, HashSet<ProtoPartResourceSnapshot> modified) {
			Debug.Log("AddResource called, adding " + amount + " " + name);
			
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
				if (d.panelData == null) {
					AddResource(data, d.resourceRate * TimeWarp.fixedDeltaTime, d.resourceName, modified);
				}
				else {
					CelestialBody kerbol = FlightGlobals.Bodies[0];
					RaycastHit hitInfo;
					Vector3d partPos = v.GetWorldPos3D() + d.panelData.position;
					Vector3d kerbolVector = (kerbol.position - partPos).normalized;
					bool hit = Physics.Raycast(v.GetWorldPos3D(), kerbolVector, out hitInfo);

					double orientationFactor = 1;

					if (d.panelData.tracks) {
						Vector3d localPivot = (v.transform.rotation * d.panelData.orientation * d.panelData.pivotAxis).normalized;
						orientationFactor = Math.Cos(Math.PI / 2.0 - Math.Acos(Vector3d.Dot(localPivot, kerbolVector)));
					}
					else {
						Vector3d localSolarNormal = (v.transform.rotation * d.panelData.orientation * d.panelData.solarNormal).normalized;
						orientationFactor = Vector3d.Dot(localSolarNormal, kerbolVector);
					}

					orientationFactor = Math.Max(orientationFactor, 0);

					if (!hit || hitInfo.collider.gameObject == kerbol) {
						double distance = kerbol.GetAltitude(partPos);
						double solarFlux = PhysicsGlobals.SolarLuminosity / (12.566370614359172 * distance * distance);

						if (v.atmDensity > 0.0) {
							// Adjust for atmosphere
						}

						double multiplier = 1;
						if (d.panelData.usesCurve) {multiplier = d.panelData.powerCurve.Evaluate((float)kerbol.GetAltitude(partPos));}
						else { multiplier = solarFlux / PhysicsGlobals.SolarLuminosityAtHome; }
						AddResource(data, d.resourceRate * TimeWarp.fixedDeltaTime * (float)orientationFactor * d.panelData.tempCurve.Evaluate(d.panelData.temperature), d.resourceName, modified);
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
						if (!v.loaded) {
							if (!vesselData.ContainsKey(v)) {
								if (!CanGetVesselData(v)) { continue; }

								vesselData.Add(v, GetVesselData(v));
								foreach (CallbackPair p in vesselData[v].callbacks.Keys) {
									moduleHandlers[p.moduleName].Load(v, p.partFlightID, ref vesselData[v].callbacks[p].data);
								}
							}

							HandleResources(v);

							foreach (CallbackPair p in vesselData[v].callbacks.Keys) {
								moduleHandlers[p.moduleName].Invoke(v, p.partFlightID, RequestBackgroundResource, ref vesselData[v].callbacks[p].data);
							}
						}
						else {vesselData.Remove(v);}
					}
				}
			}
		}

		private void OnSave(ConfigNode persistence) {
			Debug.Log("BackgroundProcessing: Saving game state");
			if (FlightGlobals.fetch != null) {
				List<Vessel> vessels = new List<Vessel>(FlightGlobals.Vessels);
				vessels.Remove(FlightGlobals.ActiveVessel);

				foreach (Vessel v in vessels) {
					if (vesselData.ContainsKey(v)) {
						foreach (CallbackPair p in vesselData[v].callbacks.Keys) {
							moduleHandlers[p.moduleName].Save(v, p.partFlightID, vesselData[v].callbacks[p].data);
						}
					}
				}
			}
		}

		private void ClearVesselData(GameScenes scene) {
			Debug.Log("BackgroundProcessing: Clearing vessel data");
			OnSave(null);
			vesselData.Clear();
		}
    }
}
