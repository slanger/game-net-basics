using GameNetBasicsCommon;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameNetBasicsServer
{
	// This class handles the network connections and game state of a single client.
	class Client : IDisposable
	{
		private const int INITIAL_PLAYER_X = 375;
		private const int INITIAL_PLAYER_Y = 200;
		private const int PLAYER_SPEED = 4;  // pixels per frame

		// The settings channel is a TCP connection with clients for relaying settings info (e.g.
		// the artificial network round-trip delay and jitter).
		public TcpClient SettingsChannel
		{
			get; private set;
		}

		// The update channel is a UDP connection with clients for relaying game state info (e.g.
		// the current position of the client's character).
		public UdpClient UpdateChannel
		{
			get; private set;
		}

		// The X position of the client's character.
		public int XPosition
		{
			get; private set;
		}

		// The Y position of the client's character.
		public int YPosition
		{
			get; private set;
		}

		// A buffer for storing frames of client input. Input frames are processed in batches
		// a game loop update.
		private List<InputFrame> _inputFrames = new List<InputFrame>(20);
		// An object to lock for protecting updates to _inputFrames.
		private readonly object _inputFramesLock = new object();
		
		// The amount of network round-trip delay (in milliseconds) to artificially add to game
		// state updates.
		private int _roundtripDelayMillis;
		// The amount of network jitter (in milliseconds) to artificially add to game state
		// updates.
		private int _jitterMillis;
		// A random number generator used for generating random amounts of network jitter.
		private readonly Random _rng = new Random();

		public Client(TcpClient settingsChannel)
		{
			SettingsChannel = settingsChannel;
			XPosition = INITIAL_PLAYER_X;
			YPosition = INITIAL_PLAYER_Y;
		}

		public void SetUpChannels()
		{
			SetUpSettingsChannel();
			SetUpUpdateChannel();
		}

		public void Update()
		{
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
					YPosition -= PLAYER_SPEED;
				if (input.DownPressed)
					YPosition += PLAYER_SPEED;
				if (input.LeftPressed)
					XPosition -= PLAYER_SPEED;
				if (input.RightPressed)
					XPosition += PLAYER_SPEED;
			}

			SendGameStateToClient();
		}

		public void Dispose()
		{
			UpdateChannel?.Dispose();
			SettingsChannel?.Dispose();
		}

		// Establishes the TCP settings channel with the client via the connection protocol, and
		// then kicks off a new thread to listen for client settings updates.
		private void SetUpSettingsChannel()
		{
			SendInitialSettingsToClient();
			ReceiveInitialSettingsFromClient();
			var thread = new Thread(ListenForSettingsUpdatesFromClient);
			thread.Start();
		}

		private void SendInitialSettingsToClient()
		{
			// TODO: Consider not sending the initial X and Y coordinates through the settings
			// channel and rather send them through the UDP channel (where coordinates are
			// normally sent through).
			byte[] initialXBytes = BitConverter.GetBytes(INITIAL_PLAYER_X);
			byte[] initialYBytes = BitConverter.GetBytes(INITIAL_PLAYER_Y);
			byte[] data = new byte[initialXBytes.Length + initialYBytes.Length];
			int i = 0;
			initialXBytes.CopyTo(data, i);
			i += initialXBytes.Length;
			initialYBytes.CopyTo(data, i);
			SettingsChannel.GetStream().Write(data);
			Debug.WriteLine($"Sent initial settings to {SettingsChannel.Client.RemoteEndPoint} client");
		}

		private void ReceiveInitialSettingsFromClient()
		{
			var receivedBytes = new byte[2 * sizeof(int)];
			int numBytes;
			numBytes = SettingsChannel.GetStream().Read(receivedBytes);
			if (numBytes != receivedBytes.Length)
			{
				throw new InvalidOperationException(
					$"Received a settings update message of unexpected size: got {numBytes} bytes, want {receivedBytes.Length} bytes");
			}
			// Note that the below assignments are thread-safe because int32 assignments are
			// atomic in C#.
			_roundtripDelayMillis = BitConverter.ToInt32(receivedBytes, 0);
			_jitterMillis = BitConverter.ToInt32(receivedBytes, sizeof(int));
			Debug.WriteLine($"Received initial settings from {SettingsChannel.Client.RemoteEndPoint} client");
		}

		private void ListenForSettingsUpdatesFromClient()
		{
			while (ReceiveSettingsFromClient()) { }
		}

		// Receives settings from a client. Returns true if successful, false otherwise.
		private bool ReceiveSettingsFromClient()
		{
			var receivedBytes = new byte[2 * sizeof(int)];
			int numBytes;
			try
			{
				numBytes = SettingsChannel.GetStream().Read(receivedBytes);
			}
			catch (SocketException ex)
			{
				Debug.WriteLine($"Exception thrown while listening for settings updates. Stopping the listener. Exception: {ex}");
				return false;
			}
			if (numBytes == 0)
			{
				Debug.WriteLine("Settings channel is shutting down");
				return false;
			}
			if (numBytes != receivedBytes.Length)
			{
				throw new InvalidOperationException(
					$"Received a settings update message of unexpected size: got {numBytes} bytes, want {receivedBytes.Length} bytes");
			}
			// Note that the below assignments are thread-safe because int32 assignments are
			// atomic in C#.
			_roundtripDelayMillis = BitConverter.ToInt32(receivedBytes, 0);
			_jitterMillis = BitConverter.ToInt32(receivedBytes, sizeof(int));
			return true;
		}

		// Establishes the UDP update channel with the client via the connection protocol, and then
		// kicks off a new thread to listen for client input.
		private void SetUpUpdateChannel()
		{
			// The connection protocol is super basic and not robust, but it will work for this
			// project.

			// First send the IPEndPoint for the UDP channel that the server is listening on.
			UpdateChannel = new UdpClient();
			IPEndPoint listenerEndpoint = new IPEndPoint(IPAddress.Parse(Protocol.SERVER_HOSTNAME), 0);
			UpdateChannel.Client.Bind(listenerEndpoint);
			Debug.WriteLine($"Listening for client update channel connection at {UpdateChannel.Client.LocalEndPoint}");
			listenerEndpoint = (IPEndPoint)UpdateChannel.Client.LocalEndPoint;
			byte[] listenerPortBytes = BitConverter.GetBytes(listenerEndpoint.Port);
			SettingsChannel.GetStream().Write(listenerPortBytes);
			Debug.WriteLine($"Sent update channel listener port {listenerEndpoint.Port} to {SettingsChannel.Client.RemoteEndPoint} client");

			// Then receive the connection initiation message from the client.
			var clientEndpoint = new IPEndPoint(IPAddress.Any, 0);
			byte[] receivedBytes;
			receivedBytes = UpdateChannel.Receive(ref clientEndpoint);
			string receivedMessage = Encoding.ASCII.GetString(receivedBytes);
			if (receivedMessage != Protocol.CONNECTION_INITIATION)
			{
				throw new InvalidOperationException(
					$"Received unexpected update channel connection message from {clientEndpoint} client: \"{receivedMessage}\"");
			}
			Debug.WriteLine($"Received connection from {clientEndpoint} client");

			// Send the connection ACK message to the client.
			UpdateChannel.Connect(clientEndpoint);
			Debug.WriteLine($"Connected new update channel with client: {UpdateChannel.Client.LocalEndPoint} -> {UpdateChannel.Client.RemoteEndPoint}");
			byte[] connAck = Encoding.ASCII.GetBytes(Protocol.CONNECTION_ACK);
			UpdateChannel.Send(connAck, connAck.Length);
			Debug.WriteLine($"Sent connection ACK to {UpdateChannel.Client.RemoteEndPoint} client");

			var thread = new Thread(ListenForInputFromClient);
			thread.Start();
		}

		private void ListenForInputFromClient()
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
					receivedBytes = UpdateChannel.Receive(ref sender);
				}
				catch (SocketException ex)
				{
					Debug.WriteLine($"Exception thrown for {UpdateChannel.Client.RemoteEndPoint} client. Closing socket. Exception: {ex}");
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

		private void SendGameStateToClient()
		{
			byte[] xBytes = BitConverter.GetBytes(XPosition);
			byte[] yBytes = BitConverter.GetBytes(YPosition);
			var dataList = new List<byte>(xBytes.Length + yBytes.Length);
			dataList.AddRange(xBytes);
			dataList.AddRange(yBytes);
			// Send data to the client asynchronously.
			SendGameStateDataToClientAsync(dataList.ToArray());
		}

		// Sends the given game state data to the client asynchronously. Artificial network delay
		// is introduced here (i.e. _roundtripDelayMillis and _jitterMillis).
		private async void SendGameStateDataToClientAsync(byte[] data)
		{
			int jitterMillis = _rng.Next(-_jitterMillis, _jitterMillis + 1);
			int delayMillis = Math.Max((_roundtripDelayMillis / 2) + jitterMillis, 0);
			await Task.Delay(delayMillis).ConfigureAwait(false);
			try
			{
				UpdateChannel.Send(data, data.Length);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Exception thrown while sending game state to client: {ex}");
			}
		}
	}
}
