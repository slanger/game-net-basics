// This is a basic game networking client.

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameNetBasicsCommon;
using System.Collections.Generic;

namespace GameNetBasicsClient
{
	public class ClientGame : Game
	{
		private const int INITIAL_ROUNDTRIP_DELAY_MILLIS = 200;
		private const int INITIAL_JITTER_MILLIS = 25;
		private const int ROUNDTRIP_DELAY_INCREMENT_MILLIS = 20;
		private const int JITTER_INCREMENT_MILLIS = 5;

		private GraphicsDeviceManager _graphics;
		private SpriteBatch _spriteBatch;
		private SpriteFont _debugFont;
		private Texture2D _playerTexture;
		private Rectangle _playerCollider = new Rectangle(0, 0, Protocol.PLAYER_WIDTH, Protocol.PLAYER_HEIGHT);
		private double _framerate;
		private readonly Random _rng = new Random();

		private UdpClient _client;
		private ClientState _clientState;
		private readonly object _clientStateLock = new object();

		private TcpClient _settingsChannel;
		private int _roundtripDelayMillis = INITIAL_ROUNDTRIP_DELAY_MILLIS;
		private int _jitterMillis = INITIAL_JITTER_MILLIS;
		private bool _sendDelayKeyPressed = false;
		private bool _sendJitterKeyPressed = false;

		public ClientGame()
		{
			_graphics = new GraphicsDeviceManager(this);
			Content.RootDirectory = "Content";
			IsMouseVisible = true;

			// Fix the game at 60 FPS. Game updates are dependent on a fixed frame rate, for
			// simplicity.
			IsFixedTimeStep = true;
			TargetElapsedTime = TimeSpan.FromSeconds(1d / 60d);
		}

		protected override void Initialize()
		{
			InitializeServerConnection();
			base.Initialize();
		}

		protected override void LoadContent()
		{
			_spriteBatch = new SpriteBatch(GraphicsDevice);
			_debugFont = Content.Load<SpriteFont>("Debug");
			_playerTexture = new Texture2D(GraphicsDevice, 1, 1);
			_playerTexture.SetData(new[] { Color.Green });
			base.LoadContent();
		}

		protected override void Dispose(bool disposing)
		{
			_client?.Dispose();
			_settingsChannel?.Dispose();
			base.Dispose(disposing);
		}

		protected override void Update(GameTime gameTime)
		{
			_framerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			KeyboardState keyState = Keyboard.GetState();
			if (keyState.IsKeyDown(Keys.Escape))
				Exit();

			int oldDelay = _roundtripDelayMillis;
			int oldJitter = _jitterMillis;

			if (keyState.IsKeyDown(Keys.D))
			{
				if (!_sendDelayKeyPressed)
				{
					_sendDelayKeyPressed = true;
					if (keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift))
						_roundtripDelayMillis = Math.Max(_roundtripDelayMillis - ROUNDTRIP_DELAY_INCREMENT_MILLIS, 0);
					else
						_roundtripDelayMillis = Math.Min(_roundtripDelayMillis + ROUNDTRIP_DELAY_INCREMENT_MILLIS, 60000);
				}
			}
			else
			{
				_sendDelayKeyPressed = false;
			}

			if (keyState.IsKeyDown(Keys.J))
			{
				if (!_sendJitterKeyPressed)
				{
					_sendJitterKeyPressed = true;
					if (keyState.IsKeyDown(Keys.LeftShift) || keyState.IsKeyDown(Keys.RightShift))
						_jitterMillis = Math.Max(_jitterMillis - JITTER_INCREMENT_MILLIS, 0);
					else
						_jitterMillis = Math.Min(_jitterMillis + JITTER_INCREMENT_MILLIS, 60000);
				}
			}
			else
			{
				_sendJitterKeyPressed = false;
			}

			if (_roundtripDelayMillis != oldDelay || _jitterMillis != oldJitter)
				SendSettingsToServer(runAsync: true);

			ClientState state;
			lock (_clientStateLock)
			{
				// Copy client state to a local variable.
				state = _clientState;
			}

			bool upPressed = false;
			bool downPressed = false;
			bool leftPressed = false;
			bool rightPressed = false;
			if (keyState.IsKeyDown(Keys.Up))
				upPressed = true;
			if (keyState.IsKeyDown(Keys.Down))
				downPressed = true;
			if (keyState.IsKeyDown(Keys.Left))
				leftPressed = true;
			if (keyState.IsKeyDown(Keys.Right))
				rightPressed = true;
			SendInput(upPressed, downPressed, leftPressed, rightPressed);

			_playerCollider.X = state.X;
			_playerCollider.Y = state.Y;

			base.Update(gameTime);
		}

		protected override void Draw(GameTime gameTime)
		{
			double drawFramerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			GraphicsDevice.Clear(Color.CornflowerBlue);
			_spriteBatch.Begin();
			_spriteBatch.Draw(_playerTexture, _playerCollider, Color.White);
			// TODO: Add a brief description of the controls.
			_spriteBatch.DrawString(
				spriteFont: _debugFont,
				text: $"FPS: {_framerate:0.00}, {drawFramerate:0.00}\nRound-trip delay: {_roundtripDelayMillis}ms\nJitter: +/-{_jitterMillis}ms",
				position: new Vector2(5, 5),
				color: new Color(31, 246, 31));
			_spriteBatch.End();
			base.Draw(gameTime);
		}

		protected override void OnExiting(object sender, EventArgs args)
		{
			Debug.WriteLine($"Closing connection: {_client.Client.LocalEndPoint} -> {_client.Client.RemoteEndPoint}");
			_client.Close();
			base.OnExiting(sender, args);
		}

		// Initializes the connection (via UDP sockets) with the server. Several messages are sent
		// back and forth as part of the connection protocol.
		private void InitializeServerConnection()
		{
			// The connection protocol is super basic and not robust, but it will work for this
			// project.

			// First we set up the settings channel and get the initial settings.
			SetUpSettingsChannel();

			// Next we receive the server's update channel port in the settings channel.
			int serverConnPort = ReceiveUpdateChannelPortFromServer();

			// Then we set up the client connection by sending the CONNECTION_INITIATION message to
			// the server's connection port.
			_client = new UdpClient();
			var serverConnEndpoint = new IPEndPoint(IPAddress.Parse(Protocol.SERVER_HOSTNAME), serverConnPort);
			byte[] connBytes = Encoding.ASCII.GetBytes(Protocol.CONNECTION_INITIATION);
			try
			{
				_client.Send(connBytes, connBytes.Length, serverConnEndpoint);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while sending connection initiation message to {serverConnEndpoint} server: {ex}");
				throw ex;
			}
			Debug.WriteLine($"Sent connection request: {_client.Client.LocalEndPoint} -> {serverConnEndpoint}");
			// Then we should receive the CONNECTION_ACK message from the server.
			var serverUpdatesEndpoint = new IPEndPoint(IPAddress.Any, 0);
			byte[] receivedBytes;
			try
			{
				receivedBytes = _client.Receive(ref serverUpdatesEndpoint);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while trying to receive connection ACK from {serverConnEndpoint} server: {ex}");
				throw ex;
			}
			string receivedMessage = Encoding.ASCII.GetString(receivedBytes);
			if (receivedMessage != Protocol.CONNECTION_ACK)
			{
				string errorMessage = $"Unrecognized connection ACK message from {serverUpdatesEndpoint} server: \"{receivedMessage}\"";
				Debug.WriteLine(errorMessage);
				throw new InvalidOperationException(errorMessage);
			}
			Debug.WriteLine($"Received ACK from server. Connecting to {serverUpdatesEndpoint}");

			// We connect to the port that the server sent the ACK message on. This will be a
			// different port than the connection port.
			try
			{
				_client.Connect(serverUpdatesEndpoint);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while connecting to {serverUpdatesEndpoint} server: {ex}");
				throw ex;
			}
			Debug.WriteLine($"Connected client to server: {_client.Client.LocalEndPoint} -> {serverUpdatesEndpoint}");
			// Start a separate thread to listen for game state updates from the server.
			var thread = new Thread(ListenForStateUpdates);
			thread.Start(serverUpdatesEndpoint);
		}

		// Sets up the settings channel and receives the initial settings.
		private void SetUpSettingsChannel()
		{
			// Connect to the server's settings channel.
			_settingsChannel = new TcpClient();
			try
			{
				_settingsChannel.Connect(Protocol.SERVER_HOSTNAME, Protocol.SETTINGS_CHANNEL_PORT);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while trying to connect to {_settingsChannel.Client.RemoteEndPoint} server's settings channel: {ex}");
				throw ex;
			}
			Debug.WriteLine($"Connected settings channel: {_settingsChannel.Client.LocalEndPoint} -> {_settingsChannel.Client.RemoteEndPoint}");

			// Receive initial settings from the server.
			byte[] settingsBytes = new byte[2 * sizeof(int)];
			int numBytes;
			try
			{
				numBytes = _settingsChannel.GetStream().Read(settingsBytes);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while trying to receive initial settings from {_settingsChannel.Client.RemoteEndPoint} server: {ex}");
				throw ex;
			}
			if (numBytes != settingsBytes.Length)
			{
				string errorMessage = $"Unrecognized settings message from {_settingsChannel.Client.RemoteEndPoint} server: got {numBytes} bytes, want {settingsBytes.Length} bytes";
				Debug.WriteLine(errorMessage);
				throw new InvalidOperationException(errorMessage);
			}
			var state = new ClientState();
			int i = 0;
			state.X = BitConverter.ToInt32(settingsBytes, i);
			i += sizeof(int);
			state.Y = BitConverter.ToInt32(settingsBytes, i);
			lock (_clientStateLock)
			{
				_clientState = state;
			}
			Debug.WriteLine("Received initial settings from the server");

			// Send initial settings to the server.
			SendSettingsToServer(runAsync: false);
			Debug.WriteLine("Sent initial settings to the server");
		}

		private int ReceiveUpdateChannelPortFromServer()
		{
			var receivedBytes = new byte[sizeof(int)];
			int numBytes;
			numBytes = _settingsChannel.GetStream().Read(receivedBytes);
			if (numBytes != receivedBytes.Length)
			{
				throw new InvalidOperationException(
					$"Received an update channel port message of unexpected size: got {numBytes} bytes, want {receivedBytes.Length} bytes");
			}
			int updateChannelPort = BitConverter.ToInt32(receivedBytes);
			Debug.WriteLine($"Received update channel port from the server: {updateChannelPort}");
			return updateChannelPort;
		}

		// Listens for game state updates from the server. When an update arrives, it is stored
		// until this client's next Update call.
		private void ListenForStateUpdates(object data)
		{
			var sender = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				// When the socket is closed, Receive() will throw an exception, and then the
				// client will stop listening for server state updates. There's probably a way to
				// do this more gracefully (e.g. using a cancellation token to cancel any ongoing
				// ReceiveAsync() calls), but this will do for now.
				byte[] receivedBytes;
				try
				{
					receivedBytes = _client.Receive(ref sender);
				}
				catch (SocketException ex)
				{
					Debug.WriteLine($"Exception thrown while listening for game state updates. Stopping the listener. Exception: {ex}");
					return;
				}
				if (receivedBytes.Length != sizeof(int) * 2)
				{
					throw new InvalidOperationException(
						$"Received a game state update message of unexpected size: got {receivedBytes.Length} bytes, want {sizeof(int) * 2} bytes");
				}
				// Messages received from the server are assumed to be the X and Y coordinates of
				// the player.
				var state = new ClientState();
				state.X = BitConverter.ToInt32(receivedBytes, 0);
				state.Y = BitConverter.ToInt32(receivedBytes, sizeof(int));
				lock (_clientStateLock)
				{
					_clientState = state;
				}
			}
		}

		// Sends the current settings to the server.
		private void SendSettingsToServer(bool runAsync)
		{
			byte[] delayBytes = BitConverter.GetBytes(_roundtripDelayMillis);
			byte[] jitterBytes = BitConverter.GetBytes(_jitterMillis);
			var dataList = new List<byte>(delayBytes.Length + jitterBytes.Length);
			dataList.AddRange(delayBytes);
			dataList.AddRange(jitterBytes);
			if (runAsync)
			{
				Task.Run(() => SendSettingsDataToServer(dataList.ToArray())).ConfigureAwait(false);
			}
			else
			{
				SendSettingsDataToServer(dataList.ToArray());
			}
		}

		// Sends the given settings data to the server.
		private void SendSettingsDataToServer(byte[] data)
		{
			try
			{
				_settingsChannel.GetStream().Write(data);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Exception thrown while sending settings data to server: {ex}");
			}
		}

		// Sends this client's input to be processed by the server.
		private void SendInput(bool upPressed, bool downPressed, bool leftPressed, bool rightPressed)
		{
			var data = new byte[4];
			data[0] = BitConverter.GetBytes(upPressed)[0];
			data[1] = BitConverter.GetBytes(downPressed)[0];
			data[2] = BitConverter.GetBytes(leftPressed)[0];
			data[3] = BitConverter.GetBytes(rightPressed)[0];
			// Send data to the server asynchronously.
			SendDataToServerAsync(data, "input");
		}

		// Sends the given data to the server asynchronously. Artificial network delay is
		// introduced here (i.e. _roundtripDelayMillis and _jitterMillis). dataDescription is used
		// for context in the exception message if an exception is thrown.
		private async void SendDataToServerAsync(byte[] data, string dataDescription)
		{
			int jitterMillis = _rng.Next(-_jitterMillis, _jitterMillis + 1);
			int delayMillis = Math.Max((_roundtripDelayMillis / 2) + jitterMillis, 0);
			await Task.Delay(delayMillis).ConfigureAwait(false);
			try
			{
				_client.Send(data, data.Length);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Exception thrown while sending {dataDescription} to server: {ex}");
			}
		}
	}
}
