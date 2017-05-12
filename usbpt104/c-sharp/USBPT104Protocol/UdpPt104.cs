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
      public const byte MainsFrequency = 0x30;
      public const byte StartConverting = 0x31;
      public const byte ReadEeprom = 0x32;
      public const byte Unlock = 0x33;
      public const byte KeepAlive = 0x34;

      public const string Pt104 = "PT104";
      public const string Mac = "Mac:";
      public const string Lock = "Lock:";
      public const string IpPort = "Port:";
      public const string Serial = "Serial:";
      public const string Eeprom = "EEPROM=";

      public const string CommandLock = "lock";
      public const string ResponseLock = "Lock";
      public const string Broadcast = "fff";
    }
    #endregion

    #region private fields
    private bool _disposed;
    private readonly int[]  _calib = new int[4];
    private readonly Timer _timer;
    #endregion
    
    # region Constructor and Device Discovery
    private UdpPt104(IPAddress address, string macAddress, ushort port, string serial) : base(address, port)
    {
      SerialNumber = serial;

      Locked = Lock();

      // Read the calibration data from the eeprom.
      Send(new byte[] { CommandsAndResponses.ReadEeprom});
      byte [] eeprom = Receive();
      int start = CommandsAndResponses.Eeprom.Length;
      _calib[0] = BitConverter.ToInt32(eeprom, 37+start);
      _calib[1] = BitConverter.ToInt32(eeprom, 41+start);
      _calib[2] = BitConverter.ToInt32(eeprom, 45+start);
      _calib[3] = BitConverter.ToInt32(eeprom, 49+start);

      // Ensure we have the same device that responded to the inital braodcast.
      Debug.Assert(macAddress == BitConverter.ToString(eeprom, 53 + start, 6));

      // Set mains to frequency rejection to 50Hz. Set 0x00 for 50Hz and 0x01 for 60Hz
      Send(new byte[] { CommandsAndResponses.MainsFrequency, 0 });      

      // Send the KeepAlive command every 10s to keep the link active.
      // If KeepAlive is not sent, the device will be automatically unlocked after 15s.
    
      _timer = new Timer(state => Send(new byte[] {CommandsAndResponses.KeepAlive}), null, 10000, 10000);

      // Start sending data back.
      BeginReceive(ReceiveData);
      // Lower nibble, bitfield for channel enabled eg 0x03 enabled channels 1 and 2
      // Upper nibble configures gain: 0==10kOhm, 1==375Ohm. eg 0x11 enables channel 1 on 375Ohm range
      Send(new byte[] { CommandsAndResponses.StartConverting, 0x01}); 
    }


    /// <summary>
    /// Parses the response to the find device broadcast. 
    /// </summary>
    /// <returns>
    /// An instance of a <see cref="UdpPt104"/> if it is valid and not locked
    ///  by a different host.
    /// </returns>
    private static UdpPt104 ParseBroadcastResponse(byte[] bytes, IPAddress address)
    {
      try
      {
        // Reply from all PT104’s will be "PT104 Mac:XXXXXX Lock:Y Port:ZZ" where
        //      XXXXXX is the 6 byte mac address of the pt104 replying 
        //      Y is 0x00 for unlocked and 0x01 for locked 
        //      ZZ is the port it will listen on 

        if (Encoding.ASCII.GetString(bytes, 0, CommandsAndResponses.Pt104.Length) != CommandsAndResponses.Pt104)
          return null;

        int i = CommandsAndResponses.Pt104.Length;

        while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.Mac.Length) != CommandsAndResponses.Mac)
          i++;
        i += CommandsAndResponses.Mac.Length;

        string macAddress = BitConverter.ToString(bytes, i, 6);

        while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.Lock.Length) != CommandsAndResponses.Lock)
          i++;

        i += CommandsAndResponses.Lock.Length;
        bool locked = BitConverter.ToBoolean(bytes, i);

        while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.IpPort.Length) != CommandsAndResponses.IpPort)
          i++;

        i += CommandsAndResponses.IpPort.Length;
        ushort port = (ushort)(bytes[i + 1] | bytes[i] << 8);

        while (Encoding.ASCII.GetString(bytes, i, CommandsAndResponses.Serial.Length) != CommandsAndResponses.Serial)
          i++;

        i += CommandsAndResponses.Serial.Length;
        string serial = Encoding.ASCII.GetString(bytes, i, bytes.Length - i).Trim();

        if (!locked)
          return new UdpPt104(address, macAddress, port, serial);
      }
      catch (ArgumentOutOfRangeException)
      {

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
    private bool Lock()
    {
      // After sending the lock command, the device will respond with either
      // "Lock Success" or "Lock Success (already locked to this machine)"

      Send(Encoding.ASCII.GetBytes(CommandsAndResponses.CommandLock));

      return Encoding.ASCII.GetString(Receive()).StartsWith(CommandsAndResponses.ResponseLock);
    }

    /// <summary>
    /// Finds an instance of a <see cref="UdpPt104"/> that is not yet locked.
    /// </summary>
    /// <returns><see cref="UdpPt104"/> or null if none found.</returns>
    public static UdpPt104 FindDevice()
    {
      // To discover all PT104s on network,send UDP packet to port 23 from port 23 (telnet) 
      // to destination 255.255.255.255 and data "fff" - 0x666666

      foreach (Tuple<byte[], IPAddress> tuple in BroadCast(Encoding.ASCII.GetBytes(CommandsAndResponses.Broadcast)))
      {
        UdpPt104 pt104 = ParseBroadcastResponse(tuple.Item1, tuple.Item2);
        if (pt104 != null)
        {
          if(pt104.Locked)
            return pt104;

          pt104.Dispose();
        }
      }
      return null;
    }

    #endregion 
    
    #region IDisposeable
    ~UdpPt104()
    {
      Dispose(false);
    }

    public void Dispose()
    {
      Dispose(true);
      GC.SuppressFinalize(this); 
    }
    protected virtual void Dispose(bool disposing)
    {
      if (_disposed)
        return;

      _disposed = true;

      // Free any unmanged resources
      Send(new byte[] { CommandsAndResponses.StartConverting, 0 }); // Start converting with no channels enabled == stop converting
      Send(new byte[] { CommandsAndResponses.Unlock });

      // Dispose of members if we are disposing
      if (disposing)
        _timer.Dispose();

      Close();
    }
    #endregion

    /// <summary>
    /// Parses a <see cref="byte"/> array into an <see cref="Int32"/>. 
    /// </summary>
    /// <remarks>We cannot use BitConverter here because it has the wrong endian.</remarks>
    /// <returns>the measurement</returns>
    int ParseMeasure(byte[] bytes, uint index)
    {
      return (bytes[index] << 24) | (bytes[index + 1] << 16) | (bytes[index + 2] << 8) | (bytes[index + 3]);
    }

    private void ReceiveData(byte[] receiveBytes)
    {
      // The data returned will be in the following format
      //
      // 00XXXX01XXXX02XXXX03XXXX  data from channel 1
      // 04XXXX05XXXX06XXXX07XXXX  data from channel 2
      // 08XXXX09XXXX0aXXXX0bXXXX  data from channel 3
      // 0cXXXX0dXXXX0eXXXX0fXXXX  data from channel 4
      //
      //(data format is: Measurement 0, 1, 2, 3 for respective channels)

      if (receiveBytes[0] == 0x00 && receiveBytes[5] == 0x01 && receiveBytes[10] == 0x02 && receiveBytes[15] == 0x03)
      {
        int measure0 = ParseMeasure(receiveBytes, 1);
        int measure1 = ParseMeasure(receiveBytes, 6);
        int measure2 = ParseMeasure(receiveBytes, 11);
        int measure3 = ParseMeasure(receiveBytes, 16);

        // Correct for 4-wire measurement - see programmers guide for other measurement types
        Ch1 = _calib[0] * 1e-6 * (measure3 - measure2) / ((double)measure1 - measure0);

        if (NewData != null)
          NewData(this, EventArgs.Empty);
      }
    }

    public event EventHandler NewData;
    public string SerialNumber { get; private set; }
    public double Ch1 { get; private set; }
    public bool Locked { get; private set; }
    public override string ToString()
    {
      return String.Format("Serial: {0}\tIP:{1}:{2}", SerialNumber, Address, Port);
    }
  }
}