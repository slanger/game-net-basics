﻿// This is a basic game networking server.

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

namespace GameNetBasicsServer
{
	public class ServerGame : Game
	{
		private GraphicsDeviceManager _graphics;
		private SpriteBatch _spriteBatch;
		private SpriteFont _debugFont;
		private Texture2D _playerTexture;
		private double _framerate;

		private UdpClient _connectionsListener;
		private UdpClient _client = null;
		private ClientState _clientState;
		private List<InputFrame> _inputFrames = new List<InputFrame>(20);
		private readonly object _inputFramesLock = new object();

		private const int INITIAL_X = 375;
		private const int INITIAL_Y = 200;

		private struct InputFrame
		{
			public bool UpPressed;
			public bool DownPressed;
			public bool LeftPressed;
			public bool RightPressed;
		}

		public ServerGame()
		{
			_graphics = new GraphicsDeviceManager(this);
			Content.RootDirectory = "Content";
			IsMouseVisible = true;

			// The server runs at a slower framerate to conserve network bandwidth.
			IsFixedTimeStep = true;
			TargetElapsedTime = TimeSpan.FromSeconds(1d / 20d);
		}

		protected override void Initialize()
		{
			InitializeServer();
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
			_connectionsListener?.Dispose();
			_client?.Dispose();
			base.Dispose(disposing);
		}

		protected override void Update(GameTime gameTime)
		{
			_framerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			KeyboardState keyState = Keyboard.GetState();
			if (keyState.IsKeyDown(Keys.Escape))
				Exit();

			InputFrame[] inputs;
			lock (_inputFramesLock)
			{
				inputs = new InputFrame[_inputFrames.Count];
				_inputFrames.CopyTo(inputs);
				_inputFrames.Clear();
			}

			foreach (InputFrame input in inputs)
			{
				if (input.UpPressed)
					_clientState.Y -= Protocol.PLAYER_SPEED;
				if (input.DownPressed)
					_clientState.Y += Protocol.PLAYER_SPEED;
				if (input.LeftPressed)
					_clientState.X -= Protocol.PLAYER_SPEED;
				if (input.RightPressed)
					_clientState.X += Protocol.PLAYER_SPEED;
			}

			if (_client != null)
				SendStateToClient();

			base.Update(gameTime);
		}

		protected override void Draw(GameTime gameTime)
		{
			double drawFramerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			GraphicsDevice.Clear(Color.CornflowerBlue);
			_spriteBatch.Begin();
			if (_client != null)
			{
				_spriteBatch.Draw(
					texture: _playerTexture,
					destinationRectangle: new Rectangle(_clientState.X, _clientState.Y, Protocol.PLAYER_WIDTH, Protocol.PLAYER_HEIGHT),
					color: Color.White);
			}
			_spriteBatch.DrawString(
				spriteFont: _debugFont,
				text: $"FPS: {_framerate:0.00}, {drawFramerate:0.00}",
				position: new Vector2(5, 5),
				color: new Color(31, 246, 31));
			_spriteBatch.End();

			base.Draw(gameTime);
		}

		// Initializes the UDP socket that listens for connections from clients on a separate
		// thread.
		// TODO: Should probably use TCP for establishing the client connections, and then UDP for
		// game state updates.
		private void InitializeServer()
		{
			_connectionsListener = new UdpClient(Protocol.CONNECTION_PORT);
			var connectionsThread = new Thread(ListenForClients);
			connectionsThread.Start();
		}

		// Listens for connections from clients on the connection port. Starts a new thread for
		// each client to listen for client input.
		private void ListenForClients()
		{
			var sender = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				Debug.WriteLine($"Listening for client connections at {_connectionsListener.Client.LocalEndPoint}");
				// When the socket is closed, Receive() will throw an exception, and then the
				// server will stop listening for client connections. There's probably a way to do
				// this more gracefully (e.g. using a cancellation token to cancel any ongoing
				// ReceiveAsync() calls), but this will do for now.
				byte[] receivedBytes;
				try
				{
					receivedBytes = _connectionsListener.Receive(ref sender);
				}
				catch (SocketException ex)
				{
					Debug.WriteLine($"Exception thrown while listening for client connections: {ex}");
					return;
				}
				string receivedMessage = Encoding.ASCII.GetString(receivedBytes);
				if (receivedMessage != Protocol.CONNECTION_INITIATION)
				{
					Debug.WriteLine($"!! Unrecognized connection message from {sender} client: \"{receivedMessage}\"");
					continue;
				}
				Debug.WriteLine($"Received connection from {sender} client");
				var thread = new Thread(HandleClient);
				thread.Start(sender);
			}
		}

		// Establishes a connection with the client via the connection protocol, and then listens
		// for client input. 
		private void HandleClient(object clientEndpointObj)
		{
			// The connection protocol is super basic and not robust, but it will work for this
			// project.
			// TODO: Handle more than one client.
			IPEndPoint clientEndpoint = (IPEndPoint)clientEndpointObj;
			Debug.WriteLine($"Created new thread for {clientEndpoint} client");

			// Set up client connection.
			var client = new UdpClient();
			try
			{
				client.Connect(clientEndpoint);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while connecting to {clientEndpoint} client: {ex}");
				return;
			}
			Debug.WriteLine($"Connected new client channel: {client.Client.LocalEndPoint} -> {client.Client.RemoteEndPoint}");
			byte[] connAck = Encoding.ASCII.GetBytes(Protocol.CONNECTION_ACK);
			try
			{
				client.Send(connAck, connAck.Length);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while sending connection ACK to {clientEndpoint} client: {ex}");
				return;
			}
			Debug.WriteLine($"Sent connection ACK to {clientEndpoint} client");
			// Need to store the client in _client AFTER it's all set up, because _client is used
			// in another thread and we don't want to send game state updates on an uninitialized
			// socket.
			_clientState = new ClientState();
			_clientState.X = INITIAL_X;
			_clientState.Y = INITIAL_Y;
			_client = client;
			ListenForClientInput(client);
		}

		// Listens for input from the client.
		private void ListenForClientInput(UdpClient client)
		{
			var sender = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				// When the socket is closed, Receive() will throw an exception, and then the
				// server will stop listening for client input. There's probably a way to do this
				// more gracefully (e.g. using a cancellation token to cancel any ongoing
				// ReceiveAsync() calls), but this will do for now.
				byte[] receivedBytes;
				try
				{
					receivedBytes = client.Receive(ref sender);
				}
				catch (SocketException ex)
				{
					Debug.WriteLine($"Exception thrown for {client.Client.RemoteEndPoint} client. Closing socket. Exception: {ex}");
					return;
				}
				if (receivedBytes.Length != sizeof(bool) * 4)
				{
					Debug.WriteLine($"!! Received a message of unexpected size: got {receivedBytes.Length}, want {sizeof(bool) * 4}");
					continue;
				}
				// Messages received from the clients are assumed to be the 4 bools representing
				// which keys have been pressed.
				var input = new InputFrame();
				input.UpPressed = BitConverter.ToBoolean(receivedBytes, 0);
				input.DownPressed = BitConverter.ToBoolean(receivedBytes, sizeof(bool));
				input.LeftPressed = BitConverter.ToBoolean(receivedBytes, sizeof(bool) * 2);
				input.RightPressed = BitConverter.ToBoolean(receivedBytes, sizeof(bool) * 3);
				lock (_inputFramesLock)
				{
					_inputFrames.Add(input);
				}
			}
		}

		// Sends the current game state to the client.
		private void SendStateToClient()
		{
			byte[] xBytes = BitConverter.GetBytes(_clientState.X);
			byte[] yBytes = BitConverter.GetBytes(_clientState.Y);
			var dataList = new List<byte>(xBytes.Length + yBytes.Length);
			dataList.AddRange(xBytes);
			dataList.AddRange(yBytes);
			// Send data to the client asynchronously.
			Task.Run(() => SendDataToClient(dataList.ToArray(), "gamestate")).ConfigureAwait(false);
		}

		// Sends the given data to the client. dataDescription is used for context in the exception
		// message if an exception is thrown.
		private void SendDataToClient(byte[] data, string dataDescription)
		{
			try
			{
				_client.Send(data, data.Length);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Exception thrown while sending {dataDescription} to client: {ex}");
			}
		}
	}
}
