using H3MP.Client.Extensions;
using H3MP.Common.Messages;
using H3MP.Common.Utils;

using BepInEx.Logging;

using LiteNetLib;
using LiteNetLib.Utils;
using System;

namespace H3MP.Client.Utils
{
	// Heavily inspired by Mirror Networking for Unity (I loved using this feature).
	// Sources:
	//	- Mirror (abstract): https://mirror-networking.com/docs/Articles/Guides/ClockSync.html
	//	- Mirror (source): https://github.com/vis2k/Mirror/blob/master/Assets/Mirror/Runtime/NetworkTime.cs
	/// <summary>
	///		Synchronizes the server-localized time in real time on the client.
	/// </summary>
	public class ServerTime : IUpdatable
	{
		public static void SendPing(NetPeer peer, NetDataWriter writer)
		{
			writer.PutTyped(new PingMessage(LocalTime.Now));

			peer.Send(writer, DeliveryMethod.ReliableUnordered);
		}

		/// <summary>
		///		Determines the stability of the offset before it is "squeezed" (technically never, but practically when the range is 2ms).
		///		Range is (0, 1).
		///		When it is >0.5, latest offset matters more.
		///		When it is <0.5, history offset matters more.
		/// </summary>
		private const double OFFSET_EMA_ALPHA = 2d / 10;
		private const double INTERVAL = 3;

		private readonly ManualLogSource _logger;
		private readonly NetPeer _server;
		private readonly Pool<NetDataWriter> _writers;
		private readonly LoopTimer _timer;

		private ExponentialMovingAverage _offsetAverage;
		private DoubleRange _offsetBounds;

		/// <summary>
		///		The <see cref="LocalTime.Now" /> value now, in real time, on the server.
		/// </summary>
		public double Now => LocalTime.Now - _offsetAverage.Value;

		public ServerTime(ManualLogSource logger, NetPeer server, Pool<NetDataWriter> writers, PongMessage seed) 
		{
			_logger = logger;
			_server = server;
			_writers = writers;
			_timer = new LoopTimer(INTERVAL);

			var offset = ProcessPong(seed, out _offsetBounds);
			_offsetAverage = new ExponentialMovingAverage(offset, OFFSET_EMA_ALPHA);
		}

		/// <summary>
		///		Start an offset update if enough time has passed.
		/// </summary>
		/// <param name="server">The server peer.</param>
		public void Update()
		{
			if (_timer is null || !_timer.TryReset())
			{
				return;
			}

			_writers.Borrow(out var writer);
			SendPing(_server, writer);
		}

		private double ProcessPong(PongMessage message, out DoubleRange bounds)
		{
			var now = LocalTime.Now;

			// The difference between the reply time locally (estimated) and remotely (known).
			// Calculated by getting the instant equally between when the message was sent and when it was received.
			var offset = message.ClientTime +
				(now - message.ClientTime) * 0.5 // estimated time until server response
				- message.ServerTime;

			// Basically squeeze theorem: if we absolutely know the range of the real offset, keep shrinking it until the difference is infinitely small (epsilon).
			bounds = new DoubleRange(message.ClientTime - message.ServerTime, now - message.ServerTime);

			return offset;
		}

		// This is almost a direct rip from Mirror's NetworkTime.OnClientPong(...), but at least I understand how it works.
		/// <summary>
		///		Finishes an offset update.
		/// </summary>
		/// <param name="message">The times sent by the server, including the original send time.</param>
		public void ProcessPong(PongMessage message)
		{
			var offset = ProcessPong(message, out var offsetBounds);

			_offsetBounds = _offsetBounds.Clamp(offsetBounds);

			if (!_offsetBounds.IsWithin(_offsetAverage.Value)) // Average does not exist or is out of known bounds.
			{
				_offsetAverage = new ExponentialMovingAverage(_offsetBounds.Clamp(offset), OFFSET_EMA_ALPHA);
			}
			else if (_offsetBounds.IsWithin(offset)) // New offset is within bounds.
			{
				_offsetAverage.Push(offset);
			}

			_logger.LogDebug($"Offset info: {_offsetBounds.Minimum:.000}s <= est. {_offsetAverage.Value:.000}s <= {_offsetBounds.Maximum:.000}s");
		}
	}
}
