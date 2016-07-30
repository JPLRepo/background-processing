using System;
using System.Collections.Generic;

namespace BackgroundProcessing.ResourceHandlers
{
    class Command : ResourceModuleHandler
    {
        public string resourceName { get; private set; }
        public float resourceRate { get; private set; }

        public Command(string rn = "", float rr = 0)
        {
            resourceName = rn;
            resourceRate = rr;
        }

        public static bool HasResourceGenerationData(
            PartModule m,
            ProtoPartModuleSnapshot s,
            Dictionary<String, List<ResourceModuleHandler>> resourceData,
            HashSet<String> interestingResources
        )
        {
            ModuleCommand c = (ModuleCommand)m;

            // TODO: Check for deactivated command modules

            foreach (ModuleResource mr in c.inputResources)
            {
                if (interestingResources.Contains(mr.name)) { return true; }
            }

            return false;
        }

        public static List<ResourceModuleHandler> GetResourceGenerationData(
            PartModule m,
            ProtoPartSnapshot part,
            Dictionary<String, List<ResourceModuleHandler>> resourceData,
            HashSet<String> interestingResources
        )
        {
            List<ResourceModuleHandler> ret = new List<ResourceModuleHandler>();

            ModuleCommand c = (ModuleCommand)m;
            foreach (ModuleResource mr in c.inputResources)
            {
                if (interestingResources.Contains(mr.name))
                {
                    ret.Add(new Command(mr.name, (float)-mr.rate));
                }
            }

            return ret;
        }

        public override HashSet<ProtoPartResourceSnapshot> HandleResource(Vessel v, VesselData data, HashSet<ProtoPartResourceSnapshot> modified)
        {
            return AddResource(data, resourceRate * TimeWarp.fixedDeltaTime, resourceName, modified);
        }
    }
}