using System.Collections.Generic;
using System;
using UnityEngine;

namespace BackgroundProcessing
{
    using DebugLevel = AddonConfig.DebugLevel;

    struct CallbackPair : IEquatable<CallbackPair>
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

    class VesselData
    {
        public Dictionary<CallbackPair, ObjectHolder> callbacks = new Dictionary<CallbackPair, ObjectHolder>();
        public List<ResourceModuleHandler> resourceModules = new List<ResourceModuleHandler>();
        public Dictionary<string, List<ProtoPartResourceSnapshot>> storage = new Dictionary<string, List<ProtoPartResourceSnapshot>>();

        private static bool HasResourceGenerationData(
            PartModule m,
            ProtoPartModuleSnapshot s,
            Dictionary<String, List<ResourceModuleHandler>> resourceData,
            HashSet<String> interestingResources
        )
        {
            if (m != null && m.moduleName == "ModuleDeployableSolarPanel")
            {
                return ResourceHandlers.SolarPanel.HasResourceGenerationData(m, s, resourceData, interestingResources);
            }

            if (m != null && m.moduleName == "ModuleCommand")
            {
                return ResourceHandlers.Command.HasResourceGenerationData(m, s, resourceData, interestingResources);
            }

            if (m != null && m.moduleName == "ModuleGenerator")
            {
                return ResourceHandlers.Generator.HasResourceGenerationData(m, s, resourceData, interestingResources);
            }

            return resourceData.ContainsKey(s.moduleName);
        }

        private static List<ResourceModuleHandler> GetResourceGenerationData(
            PartModule m,
            ProtoPartSnapshot part,
            Dictionary<String, List<ResourceModuleHandler>> resourceData,
            HashSet<String> interestingResources
        )
        {
            List<ResourceModuleHandler> ret = new List<ResourceModuleHandler>();

            if (m != null && m.moduleName == "ModuleGenerator")
            {
                ret.AddRange(ResourceHandlers.Generator.GetResourceGenerationData(m, part, resourceData, interestingResources));
            }

            if (m != null && m.moduleName == "ModuleDeployableSolarPanel")
            {
                ret.AddRange(ResourceHandlers.SolarPanel.GetResourceGenerationData(m, part, resourceData, interestingResources));
            }

            if (m != null && m.moduleName == "ModuleCommand")
            {
                ret.AddRange(ResourceHandlers.Command.GetResourceGenerationData(m, part, resourceData, interestingResources));
            }

            if (m != null && resourceData.ContainsKey(m.moduleName)) { ret.AddRange(resourceData[m.moduleName]); }
            return ret;
        }

        public static VesselData GetVesselData(
            Vessel v,
            Dictionary<String, UpdateHelper> moduleHandlers,
            Dictionary<String, List<ResourceModuleHandler>> resourceData,
            HashSet<String> interestingResources
        )
        {
            VesselData ret = new VesselData();

            if (v.protoVessel != null)
            {
                foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
                {
                    Part part = PartLoader.getPartInfoByName(p.partName).partPrefab;

                    if (part == null)
                    {
                        BackgroundProcessing.Debug("BackgroundProcessing: Couldn't find PartPrefab for part " + p.partName, DebugLevel.WARNING);
                        continue;
                    }

                    if (part.Modules == null) { continue; }

                    for (int i = 0; i < p.modules.Count; ++i)
                    {
                        if (p.modules[i].moduleName == null)
                        {
                            BackgroundProcessing.Debug("BackgroundProcessing: Null moduleName for module " + i + "/" + p.modules.Count, DebugLevel.WARNING);
                            BackgroundProcessing.Debug("BackgroundProcessing: Module values: " + p.modules[i].moduleValues, DebugLevel.WARNING);
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
                                BackgroundProcessing.Debug("BackgroundProcessing: Expected " + p.modules[i].moduleName + " at index " + i + ", got " + part.Modules[j].moduleName, DebugLevel.WARNING);

                                for (j = i; j < part.Modules.Count; ++j)
                                {
                                    if (part.Modules[j].moduleName == p.modules[i].moduleName)
                                    {
                                        BackgroundProcessing.Debug("BackgroundProcessing: Found " + p.modules[i].moduleName + " at index " + j, DebugLevel.WARNING);
                                        break;
                                    }
                                }
                            }
                        }

                        if (j < part.Modules.Count)
                        {
                            if (HasResourceGenerationData(part.Modules[j], p.modules[i], resourceData, interestingResources))
                            {
                                ret.resourceModules.AddRange(GetResourceGenerationData(part.Modules[j], p, resourceData, interestingResources));
                            }
                        }
                        else
                        {
                            BackgroundProcessing.Debug("BackgroundProcessing: Ran out of modules before finding module " + p.modules[i].moduleName, DebugLevel.WARNING);

                            if (HasResourceGenerationData(null, p.modules[i], resourceData, interestingResources))
                            {
                                ret.resourceModules.AddRange(GetResourceGenerationData(null, p, resourceData, interestingResources));
                            }
                        }
                    }

                    foreach (ProtoPartResourceSnapshot r in p.resources)
                    {
                        if (r.resourceName == null)
                        {
                            BackgroundProcessing.Debug("BackgroundProcessing: Null resourceName.", DebugLevel.WARNING);
                            BackgroundProcessing.Debug("BackgroundProcessing: Resource values: " + r.resourceValues, DebugLevel.WARNING);
                            continue;
                        }

                        bool flowState;

                        if (bool.TryParse(r.resourceValues.GetValue("flowState"), out flowState))
                        {
                            if (!flowState) { continue; }
                        }
                        else
                        {
                            BackgroundProcessing.Debug("BackgroundProcessing: failed to read flow state for resource " + r.resourceName, DebugLevel.WARNING);
                            BackgroundProcessing.Debug("BackgroundProcessing: Resource values: " + r.resourceValues, DebugLevel.WARNING);
                        }

                        if (!ret.storage.ContainsKey(r.resourceName)) { ret.storage.Add(r.resourceName, new List<ProtoPartResourceSnapshot>()); }
                        ret.storage[r.resourceName].Add(r);
                    }
                }
            }

            return ret;
        }
    }
}