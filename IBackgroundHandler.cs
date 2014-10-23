namespace BackgroundProcessing {
	public interface IBackgroundHandler {
		void FixedBackgroundUpdate(Vessel v, uint partFlightID);
	}
}
