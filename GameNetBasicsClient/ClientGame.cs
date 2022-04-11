// This is a basic game networking client.

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameNetBasicsCommon;

namespace GameNetBasicsClient
{
	public class ClientGame : Game
	{
		private GraphicsDeviceManager _graphics;
		private SpriteBatch _spriteBatch;
		private SpriteFont _debugFont;
		private Texture2D _playerTexture;
		private Rectangle _playerCollider = new Rectangle(
			Protocol.PLAYER_START_X, Protocol.PLAYER_START_Y, Protocol.PLAYER_WIDTH, Protocol.PLAYER_HEIGHT);
		private double _framerate;

		private UdpClient _client;
		private ClientState _clientState;
		private readonly object _clientStateLock = new object();

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
			base.Dispose(disposing);
		}

		protected override void Update(GameTime gameTime)
		{
			_framerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			KeyboardState keyState = Keyboard.GetState();
			if (keyState.IsKeyDown(Keys.Escape))
				Exit();

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
			_spriteBatch.DrawString(
				spriteFont: _debugFont,
				text: $"FPS: {_framerate:0.00}, {drawFramerate:0.00}",
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
			_client = new UdpClient();
			// First we send the CONNECTION_INITIATION message to the server's connection port.
			var serverConnEndpoint = new IPEndPoint(
				IPAddress.Parse(Protocol.CONNECTION_HOSTNAME), Protocol.CONNECTION_PORT);
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
						$"Received a game state update message of unexpected size: got {receivedBytes.Length}, want {sizeof(int) * 2}");
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

		// Sends this client's input to be processed by the server.
		private void SendInput(bool upPressed, bool downPressed, bool leftPressed, bool rightPressed)
		{
			List<byte> dataList = new List<byte>();
			dataList.AddRange(BitConverter.GetBytes(upPressed));
			dataList.AddRange(BitConverter.GetBytes(downPressed));
			dataList.AddRange(BitConverter.GetBytes(leftPressed));
			dataList.AddRange(BitConverter.GetBytes(rightPressed));
			byte[] data = dataList.ToArray();
			// Send data to the server asynchronously.
			Task.Run(() => SendDataToServer(data, "input"));
		}

		// Sends the given data to the server. dataDescription is used for context in the exception
		// message if an exception is thrown.
		private void SendDataToServer(byte[] data, string dataDescription)
		{
			try
			{
				_client.Send(data, data.Length);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while sending {dataDescription} to server: {ex}");
			}
		}
	}
}
