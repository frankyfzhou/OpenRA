#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using OpenRA.Server;

namespace OpenRA.Network
{
	/// <summary>
	/// Client-side IConnection that communicates with a local Server via in-memory queues.
	/// Used for WASM skirmish where TCP sockets are not available.
	/// Pairs with a Server.Connection created via <see cref="Server.Server.AcceptInProcessConnection"/>.
	///
	/// Instead of relying on server ACKs (which require matching sent orders via a Queue
	/// that has reliability issues in WASM), this implementation self-acknowledges orders
	/// immediately using the server's OrderLatency value.
	/// </summary>
	public sealed class InProcessClientConnection : IConnection
	{
		readonly Server.Server server;
		readonly Server.Connection serverConn;
		readonly Queue<(int Frame, OrderPacket Orders)> pendingSelfAcks = [];
		bool disposed;

		public int LocalClientId => serverConn.PlayerIndex;

		public InProcessClientConnection(Server.Server server, Server.Connection serverConn)
		{
			this.server = server;
			this.serverConn = serverConn;
		}

		void IConnection.StartGame() { }

		void IConnection.Send(int frame, IEnumerable<Order> orders)
		{
			var packet = new OrderPacket(orders);
			// Self-acknowledge: project the frame by OrderLatency (same as server does)
			var projectedFrame = frame + server.OrderLatency;
			pendingSelfAcks.Enqueue((projectedFrame, packet));

			// FeedPacket expects raw order data WITHOUT the 4-byte frame prefix.
			// The TCP path strips the frame during header parsing; we must match that format
			// so Server.ReceiveOrders can correctly identify SyncHash vs regular orders via data[0].
			var serialized = packet.Serialize(frame);
			serverConn.FeedPacket(server, frame, serialized.AsSpan(4).ToArray());
		}

		void IConnection.SendImmediate(IEnumerable<Order> orders)
		{
			var packet = new OrderPacket(orders);
			var serialized = packet.Serialize(0);
			serverConn.FeedPacket(server, 0, serialized.AsSpan(4).ToArray());
		}

		void IConnection.SendSync(int frame, int syncHash, ulong defeatState)
		{
			// Send raw sync data without frame prefix (matches TCP wire format).
			// data[0] must be OrderType.SyncHash (0x65) so Server.ReceiveOrders
			// correctly skips the OrderLatency adjustment for sync packets.
			var ms = new MemoryStream(Order.SyncHashOrderLength);
			ms.WriteByte((byte)OrderType.SyncHash);
			ms.Write(BitConverter.GetBytes(syncHash));
			ms.Write(BitConverter.GetBytes(defeatState));
			serverConn.FeedPacket(server, frame, ms.ToArray());
		}

		void IConnection.Receive(OrderManager orderManager)
		{
			// First, deliver any self-acknowledged orders
			while (pendingSelfAcks.Count > 0)
			{
				var (frame, packet) = pendingSelfAcks.Dequeue();
				orderManager.ReceiveOrders(LocalClientId, (frame, packet));
			}

			// Drain server outbound frames for non-ACK data (sync, tick scale, disconnect, etc.)
			// Frame format: [len+4 : int32] [clientId : int32] [frame(4) + data : byte[len]]
			while (serverConn.TryDequeueOutbound(out var rawFrame))
			{
				if (rawFrame.Length < 12)
					continue;

				var len = BitConverter.ToInt32(rawFrame, 0);
				var clientId = BitConverter.ToInt32(rawFrame, 4);
				var payload = new byte[len];
				Array.Copy(rawFrame, 8, payload, 0, Math.Min(len, rawFrame.Length - 8));

				var p = (FromClient: clientId, Data: payload);

				if (OrderIO.TryParseDisconnect(p, out var disconnect))
					orderManager.ReceiveDisconnect(disconnect.ClientId, disconnect.Frame);
				else if (OrderIO.TryParseSync(p.Data, out var sync))
					orderManager.ReceiveSync(sync);
				else if (OrderIO.TryParseTickScale(p, out var scale))
					orderManager.ReceiveTickScale(scale);
				else if (OrderIO.TryParsePingRequest(p, out _))
				{
					// Ignore pings for in-process connections
				}
				else if (OrderIO.TryParseAck(p, out _, out _))
				{
					// Skip server ACKs — we self-acknowledge in Send()
				}
				else if (OrderIO.TryParseOrderPacket(p.Data, out var orders))
				{
					if (orders.Frame == 0)
						orderManager.ReceiveImmediateOrders(p.FromClient, orders.Orders);
					else
						orderManager.ReceiveOrders(p.FromClient, orders);
				}

				if (disposed)
					return;
			}
		}

		ConnectionState IConnection.ConnectionState => ConnectionState.Connected;

		void IDisposable.Dispose()
		{
			if (disposed)
				return;
			disposed = true;
			serverConn.Dispose();
		}
	}
}
