using System.Collections.Generic;

namespace BackgroundProcessing {
	interface IBackgroundResourceModule {
		void GetResourceInfo(out List<string> interestingResources, out List<ResourceModuleData> resources);
	}
}
