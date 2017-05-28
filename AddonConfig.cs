using KSP.IO;
using System;

namespace BackgroundProcessing {
	public class AddonConfig {
		public enum DebugLevel {
			SILENT = 0,
			ERROR = 1,
			WARNING = 2,
			ALL = 3
		};

		public DebugLevel debugLevel { get; private set; }
		public bool solarOrientationMatters { get; private set; }
		public bool solarTemperatureMatters { get; private set; }

		public AddonConfig(PluginConfiguration config) {
			try {
				string tmp = config.GetValue<string>("DebugLevel", Enum.GetName(typeof(DebugLevel), DebugLevel.ERROR));
				debugLevel = (DebugLevel)Enum.Parse(typeof(DebugLevel), tmp);
			}
			catch (Exception) {
				debugLevel = DebugLevel.ERROR;
			}

			solarOrientationMatters = config.GetValue<bool>("SolarOrientationMatters", true);
			solarTemperatureMatters = config.GetValue<bool>("SolarTemperatureMatters", true);
		}
	}
}