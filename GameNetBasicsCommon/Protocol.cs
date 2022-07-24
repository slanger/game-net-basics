namespace GameNetBasicsCommon
{
	// This class contains constants needed for the networking protocols.
	public class Protocol
	{
		public const string SERVER_HOSTNAME = "127.0.0.1";
		public const int SETTINGS_CHANNEL_PORT = 11000;
		public const string CONNECTION_INITIATION = "GameNetBasics: CONNECT";
		public const string CONNECTION_ACK = "GameNetBasics: ACK";

		public const int PLAYER_WIDTH = 50;
		public const int PLAYER_HEIGHT = 50;
	}
}
