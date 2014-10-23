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

		private struct CallbackPair {
			public String moduleName;
			public uint partFlightID;

			public CallbackPair(String m, uint i) {
				moduleName = m;
				partFlightID = i;
			}
		};

		private class VesselData {
			public List<CallbackPair> callbacks = new List<CallbackPair>();
		}

		private VesselData GetVesselData(Vessel v)
		{
			VesselData ret = new VesselData();

			foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots) {
				foreach (ProtoPartModuleSnapshot m in p.modules) {
					if (moduleHandlers.ContainsKey(m.moduleName)) {
						ret.callbacks.Add(new CallbackPair(m.moduleName, p.flightID));
					}
				}
			}

			Debug.Log("BackgroundProcessing: Found " + ret.callbacks.Count + " background modules on vessel " + v.name);

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
		}

		public void FixedUpdate() {
			if (FlightGlobals.fetch != null) {
				List<Vessel> vessels = new List<Vessel>(FlightGlobals.Vessels);

				foreach (Vessel v in vessels) {
					if (!vesselData.ContainsKey(v)) {
						if (!CanGetVesselData(v)) {continue;}

						vesselData.Add(v, GetVesselData(v));
					}

					if (!(v.situation == Vessel.Situations.PRELAUNCH) && (!v.loaded || v.packed)) {
						
						foreach (CallbackPair p in vesselData[v].callbacks) {
							moduleHandlers[p.moduleName].FixedBackgroundUpdate(v, p.partFlightID);
						}
					}
				}
			}
		}
    }
}
