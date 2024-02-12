/*******************************************************************************
 *
 * Filename: UdpPt104.cs
 *
 * Description:
 *   Encapsulates a UDP PT-104.
 *    
 * Copyright (C) 2013 - 2017 Pico Technology Ltd. See LICENSE file for terms.    
 *    
 *******************************************************************************/

using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;

namespace USBPT104Protocol
{
	/// <summary>
	/// Encapsulates a UDP PT-104
	/// 
	/// Once this class is instanciated, the fields Ch1
	/// contains the lastest data measured in Ohms.
	/// 
	/// Subscribe to the NewData event to be notified
	/// when the Ch1 feild is updated.
	/// 
	/// See 'USB PT-104 Programmer's Guide' for more 
	/// information on the PT104 protocol.
	/// 
	/// 
	/// By default the Ethernet module is disabled to 
	/// save power, to enable plug into a USB port and 
	/// use the Ethernet settings application installed 
	/// with picolog. Once and IP and port is assigned 
	/// then the module is enabled. The unit may then 
	/// be used by powering from USB or POE.
	/// </summary>
	/// 
	internal class UdpPt104 : UdpDevice, IDisposable
	{
		#region commands and responses
		private struct CommandsAndResponses
		{
			public const Byte MainsFrequency = 0x30;
			public const Byte StartConverting = 0x31;
			public const Byte ReadEeprom = 0x32;
			public const Byte Unlock = 0x33;
			public const Byte KeepAlive = 0x34;

			public const String Pt104 = "PT104";
			public const String Mac = "Mac:";
			public const String Lock = "Lock:";
			public const String IpPort = "Port:";
			public const String Serial = "Serial:";
			public const String Eeprom = "EEPROM=";

			public const String CommandLock = "lock";
			public const String ResponseLock = "Lock";
			public const String Broadcast = "fff";
		}
		#endregion

		#region private fields
		private Boolean _disposed;
		private readonly Int32[] _calib = null;
		private readonly Timer _timer;
		private readonly Int32 _udp_size = 20;
		#endregion

		#region Constructor and Device Discovery
		public UdpPt104(IPAddress address, String macAddress, UInt16 port, String serial) : base(address, port)
		{
			this.SerialNumber = serial;

			this.Locked = this.Lock();

			// Read the calibration data from the eeprom.
			this.Send(new Byte[] { CommandsAndResponses.ReadEeprom });
			Byte[] eeprom = this.Receive();
			Int32 start = CommandsAndResponses.Eeprom.Length;
			this._calib = new Int32[UdpPt104.ChannelNum];
			this._calib[0] = BitConverter.ToInt32(eeprom, 37 + start);
			this._calib[1] = BitConverter.ToInt32(eeprom, 41 + start);
			this._calib[2] = BitConverter.ToInt32(eeprom, 45 + start);
			this._calib[3] = BitConverter.ToInt32(eeprom, 49 + start);

			// Ensure we have the same device that responded to the inital braodcast.
			Debug.Assert(macAddress == BitConverter.ToString(eeprom, 53 + start, 6));

			// Send the KeepAlive command every 10s to keep the link active.
			// If KeepAlive is not sent, the device will be automatically unlocked after 15s.

			this._timer = new Timer(state => this.Send(new Byte[] { CommandsAndResponses.KeepAlive }), null, 10000, 10000);
		}

		/// <summary>
		/// Send configuration
		/// </summary>
		public void InitConfigure(Byte cfg, Byte freq_reject)
		{
			this.Ch = new Double[UdpPt104.ChannelNum];

			// Set 0x00 for 50Hz and 0x01 for 60Hz
			this.Send(new Byte[] { CommandsAndResponses.MainsFrequency, freq_reject });

			// Start sending data back.
			this.BeginReceive(this.ReceiveData);
			// Lower nibble, bitfield for channel enabled eg 0x03 enabled channels 1 and 2
			// Upper nibble configures gain: 0==10kOhm, 1==375Ohm. eg 0x11 enables channel 1 on 375Ohm range
			this.Send(new Byte[] { CommandsAndResponses.StartConverting, cfg });
		}

		/// <summary>
		/// Parses the response to the find device broadcast. 
		/// </summary>
		/// <returns>
		/// An instance of a <see cref="UdpPt104"/> if it is valid and not locked
		///  by a different host.
		/// </returns>
		private static UdpPt104 ParseBroadcastResponse(Byte[] bytes, IPAddress address)
		{
			try
			{
				// Reply from all PT104’s will be "PT104 Mac:XXXXXX Lock:Y Port:ZZ" where
				//      XXXXXX is the 6 byte mac address of the pt104 replying 
				//      Y is 0x00 for unlocked and 0x01 for locked 
				//      ZZ is the port it will listen on 

				if (Encoding.ASCII.GetString(bytes, 0, CommandsAndResponses.Pt104.Length) != CommandsAndResponses.Pt104)
					return null;

				Int32 i = CommandsAndResponses.Pt104.Length;

				while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.Mac.Length) != CommandsAndResponses.Mac)
					i++;
				i += CommandsAndResponses.Mac.Length;

				String macAddress = BitConverter.ToString(bytes, i, 6);

				while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.Lock.Length) != CommandsAndResponses.Lock)
					i++;

				i += CommandsAndResponses.Lock.Length;
				Boolean locked = BitConverter.ToBoolean(bytes, i);

				while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.IpPort.Length) != CommandsAndResponses.IpPort)
					i++;

				i += CommandsAndResponses.IpPort.Length;
				UInt16 port = (UInt16)(bytes[i + 1] | bytes[i] << 8);

				while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.Serial.Length) != CommandsAndResponses.Serial)
					i++;

				i += CommandsAndResponses.Serial.Length;
				String serial = Encoding.ASCII.GetString(bytes, i, bytes.Length - i).Trim();

				if (!locked)
					return new UdpPt104(address, macAddress, port, serial);
			}
			catch (ArgumentOutOfRangeException)
			{
				Debug.WriteLine("ParseBroadcastResponse: ArgumentOutOfRangeException");
			}
			return null;
		}

		/// <summary>
		/// Lock the device for use by this host only.
		/// </summary>
		/// <returns>
		/// true if the device is successfully locked.
		/// false if the device is locked by another host.
		/// </returns>
		private Boolean Lock()
		{
			// After sending the lock command, the device will respond with either
			// "Lock Success" or "Lock Success (already locked to this machine)"

			this.Send(Encoding.ASCII.GetBytes(CommandsAndResponses.CommandLock));

			return Encoding.ASCII.GetString(this.Receive()).StartsWith(CommandsAndResponses.ResponseLock);
		}

		/// <summary>
		/// Finds an instance of a <see cref="UdpPt104"/> that is not yet locked.
		/// </summary>
		/// <returns><see cref="UdpPt104"/> or null if none found.</returns>
		public static UdpPt104 FindDevice()
		{
			// To discover all PT104s on all network, send UDP packet to port 23 from port 23 (telnet)
			// to destination 255.255.255.255 and data "fff" - 0x666666

			UdpPt104 pt104 = FindDeviceOnAdapter(IPAddress.Any);
			return pt104;
		}

		/// <summary>
		/// Finds an instance of a <see cref="UdpPt104"/> that is not yet locked on specific Host IP address/adapter.
		/// </summary>
		/// <returns><see cref="UdpPt104"/> or null if none found.</returns>
		public static UdpPt104 FindDeviceOnAdapter(IPAddress host_ip)
		{
			// To discover all PT104s on specific host IP address network, send UDP packet to port 23 from port 23 (telnet)

			foreach (Tuple<Byte[], IPAddress> tuple in BroadCast(host_ip, Encoding.ASCII.GetBytes(CommandsAndResponses.Broadcast)))
			{
				UdpPt104 pt104 = ParseBroadcastResponse(tuple.Item1, tuple.Item2);
				if (pt104 != null)
				{
					if (pt104.Locked)
						return pt104;

					pt104.Dispose();
				}
			}
			return null;
		}

		/// <summary>
		/// Connect and create an instance of a <see cref="UdpPt104"/>.
		/// </summary>
		/// <returns><see cref="UdpPt104"/> or null if none found.</returns>
		public static UdpPt104 ConnectDevice(IPAddress dev_ip, UInt16 dev_port)
		{
			UdpPt104 pt104 = new UdpPt104(dev_ip, "Unknown", dev_port, "Unknown");
			return pt104;
		}

		#endregion

		#region IDisposeable
		~UdpPt104()
		{
			this.Dispose(false);
		}

		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(Boolean disposing)
		{
			if (this._disposed)
				return;

			this._disposed = true;

			// Free any unmanged resources
			this.Send(new Byte[] { CommandsAndResponses.StartConverting, 0 }); // Start converting with no channels enabled == stop converting
			this.Send(new Byte[] { CommandsAndResponses.Unlock });

			// Dispose of members if we are disposing
			if (disposing)
				this._timer.Dispose();

			this.Close();
		}
		#endregion

		/// <summary>
		/// Parses a <see cref="Byte"/> array into an <see cref="Int32"/>. 
		/// </summary>
		/// <remarks>We cannot use BitConverter here because it has the wrong endian.</remarks>
		/// <returns>the measurement</returns>
		Int32 ParseMeasure(Byte[] bytes, UInt32 index)
		{
			return (bytes[index] << 24) | (bytes[index + 1] << 16) | (bytes[index + 2] << 8) | (bytes[index + 3]);
		}

		private void ReceiveData(Byte[] receiveBytes)
		{
			// The data returned will be in the following format
			//
			// 00XXXX01XXXX02XXXX03XXXX  data from channel 1
			// 04XXXX05XXXX06XXXX07XXXX  data from channel 2
			// 08XXXX09XXXX0aXXXX0bXXXX  data from channel 3
			// 0cXXXX0dXXXX0eXXXX0fXXXX  data from channel 4
			//
			//(data format is: Measurement 0, 1, 2, 3 for respective channels)

			Double raw_measure = 0.0;
			Int32 measure0 = 0;
			Int32 measure1 = 0;
			Int32 measure2 = 0;
			Int32 measure3 = 0;
			Int32 i = 0;

			if (receiveBytes.Length == this._udp_size)
			{
				measure0 = this.ParseMeasure(receiveBytes, 1);
				measure1 = this.ParseMeasure(receiveBytes, 6);
				measure2 = this.ParseMeasure(receiveBytes, 11);
				measure3 = this.ParseMeasure(receiveBytes, 16);

				i = receiveBytes[0] / UdpPt104.ChannelNum;

				// Correct for 4-wire measurement - see programmers guide for other measurement types
				raw_measure = 1e-6 * ((Double)(measure3 - measure2) / (Double)(measure1 - measure0));
				this.Ch[i] = this._calib[i] * raw_measure;

				if (NewData != null)
				{
					NewData(this, EventArgs.Empty);
				}
			}
		}

		public static Int32 ChannelNum { get; } = 4;

		public event EventHandler NewData;
		public String SerialNumber { get; private set; }
		public Double[] Ch { get; private set; }
		public Boolean Locked { get; private set; }
		public override String ToString()
		{
			return String.Format("Serial: {0}\tIP:{1}:{2}", this.SerialNumber, this.Address, this.Port);
		}
	}
}
