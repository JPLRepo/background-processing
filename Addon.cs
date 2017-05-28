// Copyright (c) 2014 James Picone
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

// Changelog
// - formatting
// - renamed plugin from 'Addon' to 'BackgroundProcessing', to assure most-recent-assembly loading against version 0.4.1.0
// - use hard-coded solar flux value when not available (it is 0 before loading first vessel in a game session)
// - enabled background update during prelaunch (EXPERIMENTAL, seems to work)
// - do not use KSP raycasting anymore, instead do sphere raycasting against celestial bodies directly
// - raycast only once per-vessel, ignore part position in raycasting (no intra-vessel occlusion was possible anyway when off-rail)
// - 0.4.2.0 - Compiled against KSP 1.1.2
// - 0.4.3.0 - Compiled against KSP 1.1.3
//   - Reduce Garbage by using startup (one time) var allocations.
// - 0.4.4.0 - Re-Factor from James Picone bitbucket
//   - Optimized Raytrace functions for SolarPanel Handler - Courtesy of ShotgunNinja
// - 0.4.5.0 - Fixes for compiling against KSP 1.2.2
//   - Fix NaN-issues while in solar orbit hopefully for real
//
// note: changes are marked with '(*_*)'
//
// Todo
// - use double precision? 


using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;
using System.Linq;
using BackgroundProcessing.ResourceHandlers;
using KSP.IO;


namespace BackgroundProcessing {


	using ResourceRequestFunc = Func<Vessel, float, string, float>;
	using DebugLevel = AddonConfig.DebugLevel;

	public delegate void BackgroundUpdateResourceFunc(Vessel v, uint partFlightId, ResourceRequestFunc resourceFunc, ref System.Object data);
	public delegate void BackgroundUpdateFunc(Vessel v, uint partFlightId, ref System.Object data);

	public delegate void BackgroundSaveFunc(Vessel v, uint partFlightId, System.Object data);
	public delegate void BackgroundLoadFunc(Vessel v, uint partFlightId, ref System.Object data);

	[KSPAddon(KSPAddon.Startup.MainMenu, true)]
	public class BackgroundProcessing : MonoBehaviour {


		private static HashSet<ProtoPartResourceSnapshot> modifiedResources = new HashSet<ProtoPartResourceSnapshot>();
		public static float RequestBackgroundResource(Vessel vessel, float amount, string resource) {
			if (!Instance.vesselData.ContainsKey(vessel)) { return 0f; }

			modifiedResources.Clear();

			ResourceModuleHandler.AddResource(Instance.vesselData[vessel], -amount, resource, modifiedResources);
			float ret = ResourceModuleHandler.ClampResource(modifiedResources);

			return amount - ret;
		}

		public static void Debug(String s, DebugLevel l) {
			if (config.debugLevel >= l) {
				switch (l) {
					case DebugLevel.ERROR: UnityEngine.Debug.LogError(s); break;
					case DebugLevel.WARNING: UnityEngine.Debug.LogWarning(s); break;
					default: UnityEngine.Debug.Log(s); break;
				}
			}
		}


		static private bool loaded = false;
		static public AddonConfig config = null;
		static public BackgroundProcessing Instance { get; private set; }


		private Dictionary<Vessel, VesselData> vesselData = new Dictionary<Vessel, VesselData>();
		private Dictionary<string, UpdateHelper> moduleHandlers = new Dictionary<string, UpdateHelper>();
		private Dictionary<string, List<ResourceModuleHandler>> resourceData = new Dictionary<string, List<ResourceModuleHandler>>();
		private HashSet<string> interestingResources = new HashSet<string>();

		public bool IsMostRecentAssembly() {
			Assembly me = Assembly.GetExecutingAssembly();

			foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies) {
				if (la.assembly.GetName().Name != me.GetName().Name) continue;

				if (la.assembly.GetName().Version > me.GetName().Version) { return false; }
			}

			return true;
		}

		public void LoadConfigFile() {
			PluginConfiguration pc = PluginConfiguration.CreateForType<BackgroundProcessing>();
			if (pc == null) { return; }

			pc.load();
			config = new AddonConfig(pc);
			pc.save();
		}

		public void Awake() {
			LoadConfigFile();

			if (loaded || !IsMostRecentAssembly()) {
				Debug("BackgroundProcessing: Assembly " + Assembly.GetExecutingAssembly().Location + " (" + Assembly.GetExecutingAssembly().GetName().Version + ") not running because:", DebugLevel.ALL);
				if (loaded) { Debug("BackgroundProcessing already loaded", DebugLevel.ALL); }
				else { Debug("IsMostRecentAssembly returned false", DebugLevel.ALL); }
				Destroy(gameObject);
				return;
			}

			Debug("BackgroundProcessing: Running assembly at " + Assembly.GetExecutingAssembly().Location + " (" + Assembly.GetExecutingAssembly().GetName().Version + ")", DebugLevel.WARNING);

			Instance = this;
			loaded = true;
			DontDestroyOnLoad(this);

			interestingResources.Add("ElectricCharge");

			HashSet<String> processed = new HashSet<String>();

			foreach (AvailablePart a in PartLoader.LoadedPartsList) {
				if (a.partPrefab.Modules != null) {
					foreach (PartModule m in a.partPrefab.Modules) {
						if (processed.Contains(m.moduleName)) continue;
						processed.Add(m.moduleName);

						MethodInfo lf = m.GetType().GetMethod("BackgroundLoad", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object).MakeByRefType() }, null);
						MethodInfo sf = m.GetType().GetMethod("BackgroundSave", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object) }, null);

						MethodInfo fbu = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object).MakeByRefType() }, null);
						MethodInfo fbur = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[4] { typeof(Vessel), typeof(uint), typeof(ResourceRequestFunc), typeof(System.Object).MakeByRefType() }, null);
						if (fbur != null) {
							moduleHandlers[m.moduleName] = new UpdateHelper
							(
							  (BackgroundUpdateResourceFunc)Delegate.CreateDelegate(typeof(BackgroundUpdateResourceFunc), fbur),
								lf != null ? (BackgroundLoadFunc)Delegate.CreateDelegate(typeof(BackgroundLoadFunc), lf) : null,
								sf != null ? (BackgroundSaveFunc)Delegate.CreateDelegate(typeof(BackgroundSaveFunc), sf) : null
							);
						}
						else {
							if (fbu != null) {
								moduleHandlers[m.moduleName] = new UpdateHelper
								(
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

							resourceData[m.moduleName] = new List<ResourceModuleHandler>();
							for (int count = (int)prc.Invoke(null, null); count > 0; --count) {
								resourceParams[0] = count;
								pr.Invoke(null, resourceParams);

								resourceData[m.moduleName].Add(new ResourceHandlers.CustomHandler((string)resourceParams[1], (float)resourceParams[2]));
							}
						}
					}
				}
			}

			GameEvents.onLevelWasLoaded.Add(ClearVesselData);
			GameEvents.onGameStateSave.Add(OnSave);


		}

		private void HandleResources(Vessel v) {
			VesselData data = vesselData[v];
			if (v.protoVessel.protoPartSnapshots.Count <= 0 || data.resourceModules.Count <= 0) {
				Debug("Vessel " + v.vesselName + " has no resource modules", DebugLevel.ALL);
				return;
			}

			HashSet<ProtoPartResourceSnapshot> modified = new HashSet<ProtoPartResourceSnapshot>();

			foreach (ResourceModuleHandler d in data.resourceModules) {
				modified = d.HandleResource(v, data, modified);
			}

			ResourceModuleHandler.ClampResource(modified);
		}


		/* Progression: Unloaded/packed -> loaded/packed -> loaded/unpacked -> active vessel
		 * While packed: No physics. Position of stuff is handled by orbital information
		 * While loaded: Parts exist, resources are handled.
		 * 
		 * Load at 2.5 km, unpack at 300 m (except for landed stuff)
		 */

		public void FixedUpdate() {
			if (FlightGlobals.fetch != null) {
				var vessels = FlightGlobals.Vessels;
				var active = FlightGlobals.ActiveVessel;
				for (int i = 0; i < vessels.Count; i++) {
					var vessel = vessels[i];
					if (vessel != active)

					//if (v.situation != Vessel.Situations.PRELAUNCH) // (*_*) EXPERIMENTAL
					{
						if (!vessel.loaded) {
							if (!vesselData.ContainsKey(vessel)) {
								if (vessel.protoVessel == null) { continue; }

								vesselData.Add(vessel, VesselData.GetVesselData(vessel, moduleHandlers, resourceData, interestingResources));
								for (int j = 0; j < vesselData[vessel].callbacks.Keys.Count; j++) {
									var p = vesselData[vessel].callbacks.Keys.ElementAt(j);
									moduleHandlers[p.moduleName].Load(vessel, p.partFlightID, ref vesselData[vessel].callbacks[p].data);
								}
							}

							HandleResources(vessel);

							for (int k = 0; k < vesselData[vessel].callbacks.Keys.Count; k++) {
								var p = vesselData[vessel].callbacks.Keys.ElementAt(k);
								moduleHandlers[p.moduleName].Invoke(vessel, p.partFlightID, RequestBackgroundResource, ref vesselData[vessel].callbacks[p].data);
							}
						}
						else { vesselData.Remove(vessel); }
					}
				}
			}
		}

		private void OnSave(ConfigNode persistence) {
			Debug("BackgroundProcessing: Saving game state", DebugLevel.ALL);
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
			Debug("BackgroundProcessing: Clearing vessel data", DebugLevel.ALL);
			OnSave(null);
			vesselData.Clear();
		}
	}


} // BackgroundProcessing