// This is a basic game networking server.

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
using GameNetBasicsCommon;

namespace GameNetBasicsServer
{
	public class ServerGame : Game
	{
		private GraphicsDeviceManager _graphics;
		private SpriteBatch _spriteBatch;
		private Texture2D _playerTexture;
		private SpriteFont _logFont;
		private double _framerate;

		private UdpClient _connectionClient;
		private UdpClient _client = null;
		private readonly object _clientInitializationLock = new object();
		private ClientState _clientState = new ClientState();
		private List<InputFrame> _inputFrames = new List<InputFrame>(20);
		private readonly object _inputFramesLock = new object();

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
			_playerTexture = new Texture2D(GraphicsDevice, 1, 1);
			_playerTexture.SetData(new[] { Color.Green });
			_logFont = Content.Load<SpriteFont>("ServerLog");
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
			// TODO: The following disposals aren't thread safe.
			_client?.Dispose();
			_connectionClient?.Dispose();
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
			Debug.WriteLine($"Processing {inputs.Length} input frames");

			/*
			string inputString = "";
			foreach (InputFrame input in inputs)
			{
				List<string> strs = new List<string>(4);
				if (input.UpPressed)
					strs.Add("^");
				if (input.DownPressed)
					strs.Add("V");
				if (input.LeftPressed)
					strs.Add("<");
				if (input.RightPressed)
					strs.Add(">");
				inputString += string.Join(' ', strs) + ", ";
			}
			Debug.WriteLine($"{inputString}");
			*/

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

			lock (_clientInitializationLock)
			{
				if (_client != null)
					SendStateToClients();
			}

			base.Update(gameTime);
		}

		protected override void Draw(GameTime gameTime)
		{
			double drawFramerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			GraphicsDevice.Clear(Color.CornflowerBlue);
			_spriteBatch.Begin();
			_spriteBatch.DrawString(
				spriteFont: _logFont,
				text: $"FPS: {_framerate:0.00}, {drawFramerate:0.00}",
				position: new Vector2(5, 5),
				color: new Color(31, 246, 31));
			_spriteBatch.Draw(
				_playerTexture,
				new Rectangle(_clientState.X, _clientState.Y, Protocol.PLAYER_WIDTH, Protocol.PLAYER_HEIGHT),
				Color.White);
			_spriteBatch.End();

			base.Draw(gameTime);
		}

		// Initializes the UDP socket that listens for connections from clients on a separate
		// thread.
		// TODO: Should probably use TCP for establishing the client connections, and then UDP for
		// game state updates.
		private void InitializeServer()
		{
			_connectionClient = new UdpClient(Protocol.CONNECTION_PORT);
			var thread = new Thread(ListenForClients);
			thread.Start();
		}

		// Listens for connections from clients on the connection port. Starts a new thread for
		// each client to listen for client input.
		private void ListenForClients()
		{
			var sender = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				Debug.WriteLine($"Listening for client connections at {_connectionClient.Client.LocalEndPoint}");
				byte[] receivedBytes = _connectionClient.Receive(ref sender);
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
		private void HandleClient(object senderEndpoint)
		{
			// The connection protocol is super basic and not robust, but it will work for this
			// project.
			// TODO: Handle more than one client.
			IPEndPoint sender = (IPEndPoint)senderEndpoint;
			Debug.WriteLine($"Created new thread for {sender} client");
			var client = new UdpClient();
			client.Connect(sender);
			Debug.WriteLine($"Connected new socket: {client.Client.LocalEndPoint} -> {client.Client.RemoteEndPoint}");
			byte[] connAck = Encoding.ASCII.GetBytes(Protocol.CONNECTION_ACK);
			client.Send(connAck, connAck.Length);
			Debug.WriteLine($"Sent connection ACK to {sender} client");
			lock (_clientInitializationLock)
			{
				_client = client;
			}
			ListenForClientInput(client);
		}

		// Listens for input from the client.
		private void ListenForClientInput(UdpClient client)
		{
			var sender = new IPEndPoint(IPAddress.Any, 0);
			while (true)
			{
				byte[] receivedBytes = client.Receive(ref sender);
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

		// Sends the current game state to the clients.
		private void SendStateToClients()
		{
			List<byte> dataList = new List<byte>();
			dataList.AddRange(BitConverter.GetBytes(_clientState.X));
			dataList.AddRange(BitConverter.GetBytes(_clientState.Y));
			byte[] data = dataList.ToArray();
			_client.SendAsync(data, data.Length);
		}
	}
}
