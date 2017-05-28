using System;

namespace BackgroundProcessing {
	using ResourceRequestFunc = Func<Vessel, float, string, float>;

	class UpdateHelper {
		private BackgroundUpdateResourceFunc resourceFunc = null;
		private BackgroundUpdateFunc updateFunc = null;

		private BackgroundLoadFunc loadFunc = null;
		private BackgroundSaveFunc saveFunc = null;

		public UpdateHelper(BackgroundUpdateResourceFunc rf, BackgroundLoadFunc lf, BackgroundSaveFunc sf) {
			resourceFunc = rf;
			loadFunc = lf;
			saveFunc = sf;
		}

		public UpdateHelper(BackgroundUpdateFunc f, BackgroundLoadFunc lf, BackgroundSaveFunc sf) {
			updateFunc = f;
			loadFunc = lf;
			saveFunc = sf;
		}

		public void Invoke(Vessel v, uint id, ResourceRequestFunc r, ref System.Object data) {
			if (resourceFunc == null) { updateFunc.Invoke(v, id, ref data); }
			else { resourceFunc.Invoke(v, id, r, ref data); }
		}

		public void Load(Vessel v, uint id, ref System.Object data) {
			if (loadFunc != null) { loadFunc.Invoke(v, id, ref data); }
		}

		public void Save(Vessel v, uint id, System.Object data) {
			if (saveFunc != null) { saveFunc.Invoke(v, id, data); }
		}
	}
}