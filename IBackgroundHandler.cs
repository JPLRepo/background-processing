namespace BackgroundProcessing {
	public interface IBackgroundModule {
		void FixedBackgroundUpdate(Vessel v, uint partFlightID);
	}
}
