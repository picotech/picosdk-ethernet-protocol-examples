/*******************************************************************************
 *
 * Filename: UdpDevice.cs
 *
 * Description:
 *   Encapsulates a UDP port
 *    
 * Copyright (C) 2013 - 2017 Pico Technology Ltd. See LICENSE file for terms.    
 *    
 *******************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace USBPT104Protocol
{
	/// <summary>
	/// Encapsulates a UDP port
	/// </summary>
	internal class UdpDevice
	{
		public IPAddress Address { get; private set; }
		public UInt16 Port { get; private set; }

		private readonly UdpClient _udpClient = new UdpClient();
		private IPEndPoint _endPoint;

		protected UdpDevice(IPAddress address, UInt16 port)
		{
			this.Port = port;
			this.Address = address;

			// Initialise the UDP Client at the correct address
			this._endPoint = new IPEndPoint(address, port);
			this._udpClient.Connect(this._endPoint);
		}
		public void Close()
		{
			this._udpClient.Close();
		}
		protected void Send(Byte[] data)
		{
			_ = this._udpClient.Send(data, data.Length);
		}
		protected Byte[] Receive()
		{
			IPEndPoint ipEndPoint = this._endPoint;
			return this._udpClient.Receive(ref ipEndPoint);
		}

		protected void BeginReceive(Action<Byte[]> receiveData)
		{
			this._udpClient.BeginReceive(this.ReceiveData, receiveData);
		}
		private void ReceiveData(IAsyncResult ar)
		{
			try
			{
				Action<Byte[]> action = ((Action<Byte[]>)ar.AsyncState);

				Byte[] receiveBytes = this._udpClient.EndReceive(ar, ref this._endPoint);
				this._udpClient.BeginReceive(this.ReceiveData, action);
				action.Invoke(receiveBytes);
			}
			catch (ObjectDisposedException)
			{
				Debug.WriteLine("ReceiveData: ObjectDisposedException");
			}
		}

		public static IEnumerable<Tuple<Byte[], IPAddress>> BroadCast(IPAddress host_ip, Byte[] sendData)
		{
			List<Tuple<Byte[], IPAddress>> result = new List<Tuple<Byte[], IPAddress>>();
			using (UdpClient client = new UdpClient())
			{
				IPEndPoint ep = new IPEndPoint(host_ip, 23);

				client.Client.Bind(ep);

				client.Send(sendData, sendData.Length, new IPEndPoint(IPAddress.Broadcast, 23));

				Thread.Sleep(500);
				while (client.Available > 0)
				{
					IPEndPoint ep2 = ep;
					result.Add(new Tuple<Byte[], IPAddress>(client.Receive(ref ep2), ep2.Address));
				}
				client.Close();
			}
			return result;
		}

		/// <summary>
		/// Every usable IPv4 address on this host, one per interface.
		/// </summary>
		/// <remarks>
		/// Loopback and interfaces that are not up are skipped.
		/// </remarks>
		private static IEnumerable<IPAddress> LocalIPv4Addresses()
		{
			List<IPAddress> addresses = new List<IPAddress>();

			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (nic.OperationalStatus != OperationalStatus.Up ||
					nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
					continue;

				foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
				{
					if (info.Address.AddressFamily == AddressFamily.InterNetwork)
						addresses.Add(info.Address);
				}
			}
			return addresses;
		}

		/// <summary>
		/// Broadcasts <paramref name="sendData"/> from every local interface in turn
		/// and collects the replies.
		/// </summary>
		/// <remarks>
		/// A broadcast from IPAddress.Any leaves by one interface only, so on a
		/// multi-homed host a device on any other subnet would never be found.
		/// Use the overload taking a host IP address to broadcast from one
		/// interface of your choosing.
		/// </remarks>
		public static IEnumerable<Tuple<Byte[], IPAddress>> BroadCast(Byte[] sendData)
		{
			List<Tuple<Byte[], IPAddress>> result = new List<Tuple<Byte[], IPAddress>>();
			HashSet<String> alreadySeen = new HashSet<String>();

			foreach (IPAddress localAddress in LocalIPv4Addresses())
			{
				try
				{
					foreach (Tuple<Byte[], IPAddress> reply in BroadCast(localAddress, sendData))
					{
						// A device reachable from more than one interface answers more than once.
						if (alreadySeen.Add(reply.Item2.ToString()))
							result.Add(reply);
					}
				}
				catch (SocketException)
				{
					// This interface cannot be used for the broadcast - try the next one.
				}
			}
			return result;
		}
	}
}