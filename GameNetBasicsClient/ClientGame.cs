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
using GameNetBasicsCommon;
using System.Threading;

namespace GameNetBasicsClient
{
	public class ClientGame : Game
	{
		private GraphicsDeviceManager _graphics;
		private SpriteBatch _spriteBatch;
		private Texture2D _playerTexture;
		private Rectangle _playerCollider = new Rectangle(
			Protocol.PLAYER_START_X, Protocol.PLAYER_START_Y, Protocol.PLAYER_WIDTH, Protocol.PLAYER_HEIGHT);

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
			_playerTexture = new Texture2D(GraphicsDevice, 1, 1);
			_playerTexture.SetData(new[] { Color.Green });
			base.LoadContent();
		}

		protected override void UnloadContent()
		{
			base.UnloadContent();
			_playerTexture?.Dispose();
			_spriteBatch?.Dispose();
			_graphics.Dispose();
		}

		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
			if (!disposing)
				return;
			_client.Dispose();  // TODO: This disposal isn't thread safe.
		}

		protected override void Update(GameTime gameTime)
		{
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
			GraphicsDevice.Clear(Color.CornflowerBlue);
			_spriteBatch.Begin();
			_spriteBatch.Draw(_playerTexture, _playerCollider, Color.White);
			_spriteBatch.End();
			base.Draw(gameTime);
		}

		// Initializes the connection (via UDP sockets) with the server. Several messages are sent
		// back and forth as part of the connection protocol.
		private void InitializeServerConnection()
		{
			// The connection protocol is super basic and not robust, but it will work for this
			// project.
			_client = new UdpClient();
			// First we send the CONNECTION_INITIATION message to the server's connection port.
			byte[] connBytes = Encoding.ASCII.GetBytes(Protocol.CONNECTION_INITIATION);
			_client.Send(connBytes, connBytes.Length, Protocol.CONNECTION_HOSTNAME, Protocol.CONNECTION_PORT);
			Debug.WriteLine($"Sent connection request: {_client.Client.LocalEndPoint} -> {Protocol.CONNECTION_HOSTNAME}:{Protocol.CONNECTION_PORT}");
			// Then we should receive the CONNECTION_ACK message from the server.
			var sender = new IPEndPoint(IPAddress.Any, 0);
			byte[] receivedBytes = _client.Receive(ref sender);
			string receivedMessage = Encoding.ASCII.GetString(receivedBytes);
			if (receivedMessage != Protocol.CONNECTION_ACK)
			{
				Debug.WriteLine($"!! Unrecognized connection message from {sender} server: \"{receivedMessage}\"");
				return;  // TODO: Properly handle this error.
			}
			Debug.WriteLine($"Received ACK from server. Connecting to {sender}");
			// We connect to the port that the server sent the ACK message on. This will be a
			// different port than the connection port.
			_client.Connect(sender);
			Debug.WriteLine("Connected to server");
			// Start a separate thread to listen for game state updates from the server.
			var thread = new Thread(ListenForStateUpdates);
			thread.Start(sender);
		}

		// Listens for game state updates from the server. When an update arrives, it is stored
		// until this client's next Update call.
		private void ListenForStateUpdates(object data)
		{
			var sender = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				byte[] receivedBytes = _client.Receive(ref sender);
				if (receivedBytes.Length != sizeof(int) * 2)
				{
					Debug.WriteLine($"!! Received a message of unexpected size: got {receivedBytes.Length}, want {sizeof(int) * 2}");
					continue;
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
			_client.SendAsync(data, data.Length);
		}
	}
}
