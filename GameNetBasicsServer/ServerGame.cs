// This is a basic game networking server.

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
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

		// A TCP socket that listens for new clients.
		private TcpListener _clientsListener;
		// The list of connected clients.
		private List<Client> _clients = new List<Client>();
		// An object to lock for protecting updates to _clients.
		private readonly object _clientsLock = new object();

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

		protected override void Update(GameTime gameTime)
		{
			_framerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			KeyboardState keyState = Keyboard.GetState();
			if (keyState.IsKeyDown(Keys.Escape))
				Exit();

			lock (_clientsLock)
			{
				foreach (Client client in _clients)
				{
					client.Update();
				}
			}

			base.Update(gameTime);
		}

		protected override void Draw(GameTime gameTime)
		{
			double drawFramerate = 1d / gameTime.ElapsedGameTime.TotalSeconds;

			GraphicsDevice.Clear(Color.CornflowerBlue);
			_spriteBatch.Begin();
			lock (_clientsLock)
			{
				foreach (Client client in _clients)
				{
					_spriteBatch.Draw(
						texture: _playerTexture,
						destinationRectangle: new Rectangle(client.XPosition, client.YPosition, Protocol.PLAYER_WIDTH, Protocol.PLAYER_HEIGHT),
						color: Color.White);
				}
			}
			_spriteBatch.DrawString(
				spriteFont: _debugFont,
				text: $"FPS: {_framerate:0.00}, {drawFramerate:0.00}",
				position: new Vector2(5, 5),
				color: new Color(31, 246, 31));
			_spriteBatch.End();

			base.Draw(gameTime);
		}

		protected override void Dispose(bool disposing)
		{
			_clientsListener?.Stop();
			lock (_clientsLock)
			{
				foreach (Client client in _clients)
				{
					client.Dispose();
				}
				_clients.Clear();
			}
			base.Dispose(disposing);
		}

		// Initializes the TCP socket and kicks off a separte thread to listen for client
		// connections.
		private void InitializeServer()
		{
			_clientsListener = new TcpListener(IPAddress.Parse(Protocol.SERVER_HOSTNAME), Protocol.SETTINGS_CHANNEL_PORT);
			_clientsListener.Start();
			var thread = new Thread(ListenForClientConnections);
			thread.Start();
		}

		private void ListenForClientConnections()
		{
			while (true)
			{
				Debug.WriteLine($"Listening for client connections at {_clientsListener.LocalEndpoint}");
				TcpClient clientConn;
				try
				{
					clientConn = _clientsListener.AcceptTcpClient();
				}
				catch (SocketException ex)
				{
					Debug.WriteLine($"Exception thrown while listening for client connections: {ex}");
					return;
				}
				Debug.WriteLine($"Received connection from client: {clientConn.Client.LocalEndPoint} -> {clientConn.Client.RemoteEndPoint}");
				var thread = new Thread(SetUpNewClient);
				thread.Start(clientConn);
			}
		}

		private void SetUpNewClient(object clientConnObj)
		{
			TcpClient clientConn = (TcpClient)clientConnObj;
			var client = new Client(clientConn);
			client.SetUpChannels();
			lock (_clientsLock)
			{
				_clients.Add(client);
			}
			Debug.WriteLine($"New client set up. Settings channel: {client.SettingsChannel.Client.LocalEndPoint} -> {client.SettingsChannel.Client.RemoteEndPoint}. Update channel: {client.UpdateChannel.Client.LocalEndPoint} -> {client.UpdateChannel.Client.RemoteEndPoint}");
		}
	}
}
