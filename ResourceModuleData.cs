namespace BackgroundProcessing {
	class ResourceModuleData {
		public string resourceName { get; private set; }
		public float resourceRate { get; private set; }

		public ResourceModuleData(string rn = "", float rr = 0) {
			resourceName = rn;
			resourceRate = rr;
		}
	}
}