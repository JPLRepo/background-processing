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
//
// note: changes are market with '(*_*)'
//
// Todo
// - use double precision? 


using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;
using System.Linq;


namespace BackgroundProcessing
{


    using ResourceRequestFunc = Func<Vessel, float, string, float>;
    public delegate void BackgroundUpdateResourceFunc(Vessel v, uint partFlightId, ResourceRequestFunc resourceFunc, ref System.Object data);
    public delegate void BackgroundUpdateFunc(Vessel v, uint partFlightId, ref System.Object data);

    public delegate void BackgroundSaveFunc(Vessel v, uint partFlightId, System.Object data);
    public delegate void BackgroundLoadFunc(Vessel v, uint partFlightId, ref System.Object data);

    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class BackgroundProcessing : MonoBehaviour
    {
        // (*_*) log utilities
        public static void Log(string msg) { Debug.Log("[BackgroundProcessing] " + msg); }
        public static void LogError(string msg) { Debug.LogError("[BackgroundProcessing] " + msg); }
        public static void LogWarning(string msg) { /* [disabled] Debug.LogWarning("[BackgroundProcessing] " + msg); */ }
        public static void LogDebug(string msg) { /* [disabled] Debug.Log("[BackgroundProcessing] " + msg); */ }

        // (*_*)
        // calculate hit point of the ray indicated by origin + direction * t with the sphere centered at 0,0,0 and with radius 'radius'
        // if there is no hit return double max value
        // it there is an hit return distance from origin to hit point
        public static double RaytraceSphere(Vector3d origin, Vector3d direction, Vector3d center, double radius)
        {
            // operate in sphere object space, so the origin is translated by -sphere_pos
            origin -= center;

            double A = Vector3d.Dot(direction, direction);
            double B = 2.0 * Vector3d.Dot(direction, origin);
            double C = Vector3d.Dot(origin, origin) - radius * radius;
            double discriminant = B * B - 4.0 * A * C;

            // ray missed the sphere (we consider single hits as misses)
            if (discriminant <= 0.0) return double.MaxValue;

            double q = (-B - Math.Sign(B) * Math.Sqrt(discriminant)) * 0.5;
            double t0 = q / A;
            double t1 = C / q;
            double dist = Math.Min(t0, t1);

            // if sphere is behind, return maxvalue, else it is visible and distance is returned
            return dist < 0.0 ? double.MaxValue : dist;
        }

        // (*_*)
        // return true if the body is visible from the vessel
        // - vessel: vessel to test
        // - dir: normalized vector from vessel to body
        // - dist: distance from vector to body surface
        // - return: true if visible, false otherwise
        public static bool RaytraceBody(Vessel vessel, CelestialBody body, out Vector3d dir, out double dist)
        {
            // shortcuts
            CelestialBody sun = FlightGlobals.Bodies[0];
            CelestialBody mainbody = vessel.mainBody;
            CelestialBody refbody = vessel.mainBody.referenceBody;

            // generate ray parameters
            Vector3d vessel_pos = VesselPosition(vessel);
            dir = body.position - vessel_pos;
            dist = dir.magnitude;
            dir /= dist;
            dist -= body.Radius;

            // store list of occluders
            List<CelestialBody> occluders = new List<CelestialBody>();

            // do not trace against the mainbody if that is our target
            if (body != mainbody) occluders.Add(mainbody);

            // do not trace against the reference body if that is our target, or if there isn't one (eg: mainbody is the sun)
            if (body != refbody || refbody == null) occluders.Add(refbody);

            // trace against any satellites, but not when mainbody is the sun (eg: mainbody is a planet)
            // we avoid the mainbody=sun case because it has a lot of satellites and the chances of occlusion are very low
            // and they probably will occlude the sun only partially at best in that case
            if (mainbody != sun) occluders.AddRange(mainbody.orbitingBodies);

            // do the raytracing
            double min_dist = double.MaxValue;
            foreach (CelestialBody cb in occluders)
            {
                min_dist = Math.Min(min_dist, RaytraceSphere(vessel_pos, dir, cb.position, cb.Radius));
            }

            // return true if body is visible from vessel
            return dist < min_dist;
        }

        // (*_*)
        // return true if landed somewhere
        public static bool Landed(Vessel vessel)
        {
            if (vessel.loaded) return vessel.Landed || vessel.Splashed;
            else return vessel.protoVessel.landed || vessel.protoVessel.splashed;
        }

        // (*_*)
        // return vessel position
        public static Vector3d VesselPosition(Vessel vessel)
        {
            if (vessel.loaded || Landed(vessel) || double.IsNaN(vessel.orbit.inclination))
            {
                return vessel.GetWorldPos3D();
            }
            else
            {
                return vessel.orbit.getPositionAtUT(Planetarium.GetUniversalTime());
            }
        }

        public float RequestBackgroundResource(Vessel vessel, float amount, string resource)
        {
            if (!vesselData.ContainsKey(vessel)) { return 0f; }

            HashSet<ProtoPartResourceSnapshot> modified = new HashSet<ProtoPartResourceSnapshot>();

            AddResource(vesselData[vessel], -amount, resource, modified);
            float ret = ClampResource(modified);

            return amount - ret;
        }

        static private bool loaded = false;
        static private BackgroundProcessing mInstance = null;
        public BackgroundProcessing Instance
        {
            get { return mInstance; }
            private set { mInstance = value; }
        }

        class UpdateHelper
        {
            private BackgroundUpdateResourceFunc resourceFunc = null;
            private BackgroundUpdateFunc updateFunc = null;

            private BackgroundLoadFunc loadFunc = null;
            private BackgroundSaveFunc saveFunc = null;

            public UpdateHelper(BackgroundUpdateResourceFunc rf, BackgroundLoadFunc lf, BackgroundSaveFunc sf)
            {
                resourceFunc = rf;
                loadFunc = lf;
                saveFunc = sf;
            }

            public UpdateHelper(BackgroundUpdateFunc f, BackgroundLoadFunc lf, BackgroundSaveFunc sf)
            {
                updateFunc = f;
                loadFunc = lf;
                saveFunc = sf;
            }

            public void Invoke(Vessel v, uint id, ResourceRequestFunc r, ref System.Object data)
            {
                if (resourceFunc == null) { updateFunc.Invoke(v, id, ref data); }
                else { resourceFunc.Invoke(v, id, r, ref data); }
            }

            public void Load(Vessel v, uint id, ref System.Object data)
            {
                if (loadFunc != null) { loadFunc.Invoke(v, id, ref data); }
            }

            public void Save(Vessel v, uint id, System.Object data)
            {
                if (saveFunc != null) { saveFunc.Invoke(v, id, data); }
            }
        }

        private Dictionary<Vessel, VesselData> vesselData = new Dictionary<Vessel, VesselData>();
        private Dictionary<string, UpdateHelper> moduleHandlers = new Dictionary<string, UpdateHelper>();
        private Dictionary<string, List<ResourceModuleData>> resourceData = new Dictionary<string, List<ResourceModuleData>>();
        private HashSet<string> interestingResources = new HashSet<string>();

        private class SolarPanelData
        {
            public FloatCurve powerCurve { get; private set; }
            public Vector3d position { get; private set; }
            public Quaternion orientation { get; private set; }
            public Vector3d solarNormal { get; private set; }
            public Vector3d pivotAxis { get; private set; }
            public bool tracks { get; private set; }
            public bool usesCurve { get; private set; }
            public FloatCurve tempCurve { get; private set; }
            public float temperature { get; private set; }

            public SolarPanelData(FloatCurve pc, Vector3d p, Quaternion o, Vector3d sn, Vector3d pa, bool t, bool uC, FloatCurve tE, float tmp)
            {
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

        private class ResourceModuleData
        {
            public string resourceName { get; private set; }
            public float resourceRate { get; private set; }
            public SolarPanelData panelData { get; private set; }

            public ResourceModuleData(string rn = "", float rr = 0, SolarPanelData panel = null)
            {
                resourceName = rn;
                resourceRate = rr;
                panelData = panel;
            }
        }

        private struct CallbackPair : IEquatable<CallbackPair>
        {
            public string moduleName;
            public uint partFlightID;

            public CallbackPair(string m, uint i)
            {
                moduleName = m;
                partFlightID = i;
            }

            public override int GetHashCode()
            {
                return moduleName.GetHashCode() + partFlightID.GetHashCode();
            }

            public bool Equals(CallbackPair rhs)
            {
                return moduleName == rhs.moduleName && partFlightID == rhs.partFlightID;
            }
        };

        class ObjectHolder
        {
            public System.Object data;
        };

        private class VesselData
        {
            public Dictionary<CallbackPair, ObjectHolder> callbacks = new Dictionary<CallbackPair, ObjectHolder>();
            public List<ResourceModuleData> resourceModules = new List<ResourceModuleData>();
            public Dictionary<string, List<ProtoPartResourceSnapshot>> storage = new Dictionary<string, List<ProtoPartResourceSnapshot>>();
        }

        private bool HasResourceGenerationData(PartModule m, ProtoPartModuleSnapshot s)
        {
            if (m != null && m.moduleName == "ModuleDeployableSolarPanel")
            {
                if (s.moduleValues.GetValue("stateString") == ModuleDeployableSolarPanel.panelStates.EXTENDED.ToString())
                {
                    return true;
                }
            }

            if (m != null && m.moduleName == "ModuleCommand")
            {
                ModuleCommand c = (ModuleCommand)m;

                foreach (ModuleResource mr in c.inputResources)
                {
                    if (interestingResources.Contains(mr.name)) { return true; }
                }
            }

            if (m != null && m.moduleName == "ModuleGenerator")
            {
                bool active = false;
                Boolean.TryParse(s.moduleValues.GetValue("generatorIsActive"), out active);
                if (active)
                {
                    ModuleGenerator g = (ModuleGenerator)m;
                    if (g.inputList.Count <= 0)
                    {
                        foreach (ModuleResource gr in g.outputList)
                        {
                            if (interestingResources.Contains(gr.name)) return true;
                        }
                    }
                }
            }

            return resourceData.ContainsKey(s.moduleName);
        }

        private List<ResourceModuleData> GetResourceGenerationData(PartModule m, ProtoPartSnapshot part)
        {
            List<ResourceModuleData> ret = new List<ResourceModuleData>();

            if (m != null && m.moduleName == "ModuleGenerator")
            {
                ModuleGenerator g = (ModuleGenerator)m;

                if (g.inputList.Count <= 0)
                {
                    foreach (ModuleResource gr in g.outputList)
                    {
                        if (interestingResources.Contains(gr.name))
                        {
                            ret.Add(new ResourceModuleData(gr.name, (float)gr.rate));
                        }
                    }
                }
            }

            if (m != null && m.moduleName == "ModuleDeployableSolarPanel")
            {
                ModuleDeployableSolarPanel p = (ModuleDeployableSolarPanel)m;

                if (interestingResources.Contains(p.resourceName))
                {
                    Transform panel = p.part.FindModelComponent<Transform>(p.raycastTransformName);
                    Transform pivot = p.part.FindModelComponent<Transform>(p.pivotName);

                    ret.Add(new ResourceModuleData(p.resourceName, p.chargeRate, new SolarPanelData(p.powerCurve, part.position, part.rotation, panel.forward, pivot.up, p.sunTracking, p.useCurve, p.temperatureEfficCurve, (float)part.temperature)));
                }
            }

            if (m != null && m.moduleName == "ModuleCommand")
            {
                ModuleCommand c = (ModuleCommand)m;
                foreach (ModuleResource mr in c.inputResources)
                {
                    if (interestingResources.Contains(mr.name))
                    {
                        ret.Add(new ResourceModuleData(mr.name, (float)-mr.rate));
                    }
                }
            }

            if (m != null && resourceData.ContainsKey(m.moduleName)) ret.AddRange(resourceData[m.moduleName]);
            return ret;
        }

        private VesselData GetVesselData(Vessel v)
        {
            VesselData ret = new VesselData();

            if (v.protoVessel != null)
            {
                foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
                {
                    Part part = PartLoader.getPartInfoByName(p.partName).partPrefab;

                    if (part == null)
                    {
                        LogWarning("Couldn't find PartPrefab for part " + p.partName);
                        continue;
                    }

                    if (part.Modules == null) continue;

                    for (int i = 0; i < p.modules.Count; ++i)
                    {
                        if (p.modules[i].moduleName == null)
                        {
                            LogWarning("Null moduleName for module " + i + "/" + p.modules.Count);
                            LogWarning("Module values: " + p.modules[i].moduleValues);
                            continue;
                        }

                        if (moduleHandlers.ContainsKey(p.modules[i].moduleName))
                        {
                            ret.callbacks.Add(new CallbackPair(p.modules[i].moduleName, p.flightID), new ObjectHolder());
                        }

                        int j = i;
                        if (j >= part.Modules.Count || part.Modules[j].moduleName != p.modules[i].moduleName)
                        {
                            if (j < part.Modules.Count)
                            {
                                LogWarning("Expected " + p.modules[i].moduleName + " at index " + i + ", got " + part.Modules[j].moduleName);

                                for (j = i; j < part.Modules.Count; ++j)
                                {
                                    if (part.Modules[j].moduleName == p.modules[i].moduleName)
                                    {
                                        LogDebug("Found " + p.modules[i].moduleName + " at index " + j);
                                        break;
                                    }
                                }
                            }
                        }

                        if (j < part.Modules.Count)
                        {
                            if (HasResourceGenerationData(part.Modules[j], p.modules[i]))
                            {
                                ret.resourceModules.AddRange(GetResourceGenerationData(part.Modules[j], p));
                            }
                        }
                        else
                        {
                            LogWarning("Ran out of modules before finding module " + p.modules[i].moduleName);

                            if (HasResourceGenerationData(null, p.modules[i]))
                            {
                                ret.resourceModules.AddRange(GetResourceGenerationData(null, p));
                            }
                        }
                    }

                    foreach (ProtoPartResourceSnapshot r in p.resources)
                    {
                        if (r.resourceName == null)
                        {
                            LogWarning("Null resourceName.");
                            LogWarning("Resource values: " + r.resourceValues);
                            continue;
                        }

                        bool flowState;

                        if (bool.TryParse(r.resourceValues.GetValue("flowState"), out flowState))
                        {
                            if (!flowState) continue;
                        }
                        else
                        {
                            LogWarning("failed to read flow state for resource " + r.resourceName);
                            LogWarning("Resource values: " + r.resourceValues);
                        }

                        if (!ret.storage.ContainsKey(r.resourceName)) { ret.storage.Add(r.resourceName, new List<ProtoPartResourceSnapshot>()); }
                        ret.storage[r.resourceName].Add(r);
                    }
                }
            }

            return ret;
        }

        private bool CanGetVesselData(Vessel v)
        {
            return v.protoVessel != null;
        }

        public bool IsMostRecentAssembly()
        {
            Assembly me = Assembly.GetExecutingAssembly();

            foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
            {
                if (la.assembly.GetName().Name != me.GetName().Name) continue;

                if (la.assembly.GetName().Version > me.GetName().Version) { return false; }
            }

            return true;
        }

        public void Awake()
        {
            if (loaded || !IsMostRecentAssembly())
            {
                LogDebug("Assembly " + Assembly.GetExecutingAssembly().Location + " (" + Assembly.GetExecutingAssembly().GetName().Version + ") not running because:");
                if (loaded) { LogDebug("BackgroundProcessing already loaded"); }
                else { LogDebug("IsMostRecentAssembly returned false"); }
                Destroy(gameObject);
                return;
            }

            Log("Running assembly at " + Assembly.GetExecutingAssembly().Location + " (" + Assembly.GetExecutingAssembly().GetName().Version + ")");

            Instance = this;
            loaded = true;
            DontDestroyOnLoad(this);

            interestingResources.Add("ElectricCharge");

            HashSet<String> processed = new HashSet<String>();

            foreach (AvailablePart a in PartLoader.LoadedPartsList)
            {
                if (a.partPrefab.Modules != null)
                {
                    foreach (PartModule m in a.partPrefab.Modules)
                    {
                        if (processed.Contains(m.moduleName)) continue;
                        processed.Add(m.moduleName);

                        MethodInfo lf = m.GetType().GetMethod("BackgroundLoad", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object).MakeByRefType() }, null);
                        MethodInfo sf = m.GetType().GetMethod("BackgroundSave", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object) }, null);

                        MethodInfo fbu = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(Vessel), typeof(uint), typeof(System.Object).MakeByRefType() }, null);
                        MethodInfo fbur = m.GetType().GetMethod("FixedBackgroundUpdate", BindingFlags.Public | BindingFlags.Static, null, new Type[4] { typeof(Vessel), typeof(uint), typeof(ResourceRequestFunc), typeof(System.Object).MakeByRefType() }, null);
                        if (fbur != null)
                        {
                            moduleHandlers[m.moduleName] = new UpdateHelper
                            (
                              (BackgroundUpdateResourceFunc)Delegate.CreateDelegate(typeof(BackgroundUpdateResourceFunc), fbur),
                                lf != null ? (BackgroundLoadFunc)Delegate.CreateDelegate(typeof(BackgroundLoadFunc), lf) : null,
                                sf != null ? (BackgroundSaveFunc)Delegate.CreateDelegate(typeof(BackgroundSaveFunc), sf) : null
                            );
                        }
                        else
                        {
                            if (fbu != null)
                            {
                                moduleHandlers[m.moduleName] = new UpdateHelper
                                (
                                    (BackgroundUpdateFunc)Delegate.CreateDelegate(typeof(BackgroundUpdateFunc), fbu),
                                    lf != null ? (BackgroundLoadFunc)Delegate.CreateDelegate(typeof(BackgroundLoadFunc), lf) : null,
                                    sf != null ? (BackgroundSaveFunc)Delegate.CreateDelegate(typeof(BackgroundSaveFunc), sf) : null
                                );
                            }
                        }

                        MethodInfo ir = m.GetType().GetMethod("GetInterestingResources", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                        if (ir != null && ir.ReturnType == typeof(List<string>))
                        {
                            interestingResources.UnionWith((List<String>)ir.Invoke(null, null));
                        }

                        MethodInfo prc = m.GetType().GetMethod("GetBackgroundResourceCount", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                        MethodInfo pr = m.GetType().GetMethod("GetBackgroundResource", BindingFlags.Public | BindingFlags.Static, null, new Type[3] { typeof(int), typeof(string).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                        if (prc != null && pr != null && prc.ReturnType == typeof(int))
                        {
                            System.Object[] resourceParams = new System.Object[3];
                            resourceParams[1] = "";
                            resourceParams[2] = 0.0f;

                            resourceData[m.moduleName] = new List<ResourceModuleData>();
                            for (int count = (int)prc.Invoke(null, null); count > 0; --count)
                            {
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

        private HashSet<ProtoPartResourceSnapshot> AddResource(VesselData data, float amount, string name, HashSet<ProtoPartResourceSnapshot> modified)
        {
            LogDebug("AddResource called, adding " + amount + " " + name);

            if (!data.storage.ContainsKey(name)) return modified;

            bool reduce = amount < 0;

            List<ProtoPartResourceSnapshot> relevantStorage = data.storage[name];
            for (int i = 0; i < relevantStorage.Count; ++i)
            {
                ProtoPartResourceSnapshot r = relevantStorage[i];

                if (amount == 0) break;
                float n; float m;

                if (float.TryParse(r.resourceValues.GetValue("amount"), out n) && float.TryParse(r.resourceValues.GetValue("maxAmount"), out m))
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

        private float ClampResource(HashSet<ProtoPartResourceSnapshot> modified)
        {
            float ret = 0;
            foreach (ProtoPartResourceSnapshot r in modified)
            {
                float n; float m; float i;

                if (float.TryParse(r.resourceValues.GetValue("amount"), out n) && float.TryParse(r.resourceValues.GetValue("maxAmount"), out m))
                {
                    i = Mathf.Clamp(n, 0, m);
                    ret += i - n;
                    n = i;

                    r.resourceValues.SetValue("amount", n.ToString());
                }
            }

            return ret;
        }

        private void HandleResources(Vessel v)
        {
            // (*_*)
            // note: SolarLuminosity is 0 before loading first vessel is a game session, replaced by method below.
            //double solar_luminosity = PhysicsGlobals.SolarLuminosity > 0.0 ? PhysicsGlobals.SolarLuminosity : 3.1609409786213E+24;

            VesselData data = vesselData[v];
            if (v.protoVessel.protoPartSnapshots.Count <= 0 || data.resourceModules.Count <= 0)
            {
                LogDebug("Vessel " + v.vesselName + " has no resource modules");
                return;
            }

            // (*_*) determine sun visibility only once per-vessel
            Vector3d sun_dir;
            double sun_dist;
            bool in_sunlight = RaytraceBody(v, FlightGlobals.Bodies[0], out sun_dir, out sun_dist);

            HashSet<ProtoPartResourceSnapshot> modified = new HashSet<ProtoPartResourceSnapshot>();
            foreach (ResourceModuleData d in data.resourceModules)
            {
                LogDebug("Checking resource module with " + d.resourceName + " for vessel " + v.vesselName);
                if (d.panelData == null)
                {
                    LogDebug("No panel data, just adding resource");
                    AddResource(data, d.resourceRate * TimeWarp.fixedDeltaTime, d.resourceName, modified);
                }
                else
                {
                    LogDebug("Panel data, doing solar panel calcs");

                    // (*_*) changes:
                    // - use sun_dir & sun_dist as computed from custom raycasting function
                    // - assume panel position is vessel position, error is trascurable and we get to raytrace only once per-vessel

                    double orientationFactor = 1;
                    if (d.panelData.tracks)
                    {
                        Vector3d localPivot = (v.transform.rotation * d.panelData.orientation * d.panelData.pivotAxis).normalized;
                        orientationFactor = Math.Cos(Math.PI / 2.0 - Math.Acos(Vector3d.Dot(localPivot, sun_dir)));
                    }
                    else
                    {
                        Vector3d localSolarNormal = (v.transform.rotation * d.panelData.orientation * d.panelData.solarNormal).normalized;
                        orientationFactor = Vector3d.Dot(localSolarNormal, sun_dir);
                    }

                    orientationFactor = Math.Max(orientationFactor, 0);

                    if (in_sunlight)
                    {
                        double solarFlux = SolarLuminosity / (12.566370614359172 * sun_dist * sun_dist);
                        LogDebug("Pre-atmosphere flux: " + solarFlux + ", pre-atmosphere distance: " + sun_dist + ", solar luminosity: " + SolarLuminosity);

                        double staticPressure = v.mainBody.GetPressure(v.altitude);
                        LogDebug("Static pressure: " + staticPressure);

                        if (staticPressure > 0.0)
                        {
                            double density = v.mainBody.GetDensity(staticPressure, d.panelData.temperature);
                            LogDebug("density: " + density);
                            Vector3 up = FlightGlobals.getUpAxis(v.mainBody, v.vesselTransform.position).normalized;
                            double sunPower = v.mainBody.radiusAtmoFactor * Vector3d.Dot(up, sun_dir);
                            double sMult = v.mainBody.GetSolarPowerFactor(density);
                            if (sunPower < 0)
                            {
                                sMult /= Math.Sqrt(2.0 * v.mainBody.radiusAtmoFactor + 1.0);
                            }
                            else
                            {
                                sMult /= Math.Sqrt(sunPower * sunPower + 2.0 * v.mainBody.radiusAtmoFactor + 1.0) - sunPower;
                            }

                            LogDebug("Atmospheric flux adjustment: " + sMult);
                            solarFlux *= sMult;

                            LogDebug("Vessel solar flux: " + v.solarFlux);
                        }
                        else { LogDebug("No need for atmospheric adjustment"); }

                        float multiplier = 1;
                        if (d.panelData.usesCurve) { multiplier = d.panelData.powerCurve.Evaluate((float)sun_dist); }
                        else { multiplier = (float)(solarFlux / PhysicsGlobals.SolarLuminosityAtHome); }

                        LogDebug("Resource rate: " + d.resourceRate);
                        LogDebug("Vessel " + v.vesselName + " solar panel, orientation factor: " + orientationFactor + ", temperature: " + d.panelData.temperature + " solar flux: " + solarFlux);
                        float resourceAmount = d.resourceRate * (float)orientationFactor * d.panelData.tempCurve.Evaluate(d.panelData.temperature) * multiplier;
                        LogDebug("Vessel " + v.vesselName + ", adding " + resourceAmount + " " + d.resourceName + " over time " + TimeWarp.fixedDeltaTime);
                        AddResource(data, resourceAmount * TimeWarp.fixedDeltaTime, d.resourceName, modified);
                    }
                    else
                    {
                        LogDebug("Can't see Kerbol");
                    }
                }
            }

            ClampResource(modified);
        }

        // return sun luminosity
        public static double SolarLuminosity
        {
            get
            {
                // note: it is 0 before loading first vessel in a game session, we compute it in that case
                if (PhysicsGlobals.SolarLuminosity <= double.Epsilon)
                {
                    double A = FlightGlobals.GetHomeBody().orbit.semiMajorAxis;
                    return A*A*12.566370614359172*PhysicsGlobals.SolarLuminosityAtHome;
                }
                return PhysicsGlobals.SolarLuminosity;
            }
        }

        /* Progression: Unloaded/packed -> loaded/packed -> loaded/unpacked -> active vessel
         * While packed: No physics. Position of stuff is handled by orbital information
         * While loaded: Parts exist, resources are handled.
         * 
         * Load at 2.5 km, unpack at 300 m (except for landed stuff)
         */

        public void FixedUpdate()
        {
            if (FlightGlobals.fetch != null)
            {
                List<Vessel> vessels = new List<Vessel>(FlightGlobals.Vessels);
                vessels.Remove(FlightGlobals.ActiveVessel);

                foreach (Vessel v in vessels)
                {
                    //if (v.situation != Vessel.Situations.PRELAUNCH) // (*_*) EXPERIMENTAL
                    {
                        if (!v.loaded)
                        {
                            if (!vesselData.ContainsKey(v))
                            {
                                if (!CanGetVesselData(v)) continue;

                                vesselData.Add(v, GetVesselData(v));
                                foreach (CallbackPair p in vesselData[v].callbacks.Keys)
                                {
                                    moduleHandlers[p.moduleName].Load(v, p.partFlightID, ref vesselData[v].callbacks[p].data);
                                }
                            }

                            HandleResources(v);

                            foreach (CallbackPair p in vesselData[v].callbacks.Keys)
                            {
                                moduleHandlers[p.moduleName].Invoke(v, p.partFlightID, RequestBackgroundResource, ref vesselData[v].callbacks[p].data);
                            }
                        }
                        else { vesselData.Remove(v); }
                    }
                }
            }
        }

        private void OnSave(ConfigNode persistence)
        {
            LogDebug("Saving game state");
            if (FlightGlobals.fetch != null)
            {
                List<Vessel> vessels = new List<Vessel>(FlightGlobals.Vessels);
                vessels.Remove(FlightGlobals.ActiveVessel);

                foreach (Vessel v in vessels)
                {
                    if (vesselData.ContainsKey(v))
                    {
                        foreach (CallbackPair p in vesselData[v].callbacks.Keys)
                        {
                            moduleHandlers[p.moduleName].Save(v, p.partFlightID, vesselData[v].callbacks[p].data);
                        }
                    }
                }
            }
        }

        private void ClearVesselData(GameScenes scene)
        {
            LogDebug("Clearing vessel data");
            OnSave(null);
            vesselData.Clear();
        }
    }


} // BackgroundProcessing