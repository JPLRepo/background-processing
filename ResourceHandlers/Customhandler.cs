using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BackgroundProcessing.ResourceHandlers
{

    class CustomHandler : ResourceModuleHandler
    {
        public string resourceName { get; private set; }
        public float resourceRate { get; private set; }

        public CustomHandler(string rn = "", float rr = 0)
        {
            resourceName = rn;
            resourceRate = rr;
        }

        public override HashSet<ProtoPartResourceSnapshot> HandleResource(Vessel v, VesselData data, HashSet<ProtoPartResourceSnapshot> modified)
        {
            return AddResource(data, resourceRate * TimeWarp.fixedDeltaTime, resourceName, modified);
        }
    }
}