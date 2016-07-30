using System;
using System.Collections.Generic;

namespace BackgroundProcessing.ResourceHandlers
{
    class Generator : ResourceModuleHandler
    {
        public string resourceName { get; private set; }
        public float resourceRate { get; private set; }

        public Generator(string rn = "", float rr = 0)
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
            bool active = false;
            Boolean.TryParse(s.moduleValues.GetValue("generatorIsActive"), out active);
            if (active)
            {
                ModuleGenerator g = (ModuleGenerator)m;
                if (g.inputList.Count <= 0)
                {
                    foreach (ModuleResource gr in g.outputList)
                    {
                        if (interestingResources.Contains(gr.name)) { return true; }
                    }
                }
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
            ModuleGenerator g = (ModuleGenerator)m;

            if (g.inputList.Count <= 0)
            {
                foreach (ModuleResource gr in g.outputList)
                {
                    if (interestingResources.Contains(gr.name))
                    {
                        ret.Add(new Generator(gr.name, (float)gr.rate));
                    }
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