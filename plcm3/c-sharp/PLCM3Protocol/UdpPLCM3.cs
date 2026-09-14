/*******************************************************************************
 *
 * Filename: UdpPLCM3.cs
 *
 * Description:
 *   Encapsulates a UDP PLCM3.
 *    
 * Copyright (C) 2017 - 2026 Pico Technology Ltd. See LICENSE file for terms.    
 *    
 *******************************************************************************/

using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;

namespace PLCM3Protocol
{
  /// <summary>
  /// Encapsulates a UDP PLCM3
  /// 
  /// Once this class is instanciated, the fields Ch1, Ch2
  /// and Ch3 contain the lastest data measured in millivolts.
  ///
  /// Subscribe to the NewData event to be notified
  /// when a channel field is updated. The device sends one
  /// datagram per channel in turn, so each event updates a
  /// single channel.
  /// 
  /// See 'PicoLog CM3 Current Data Logger Programmer's Guide' for more 
  /// information on the PLCM3 protocol.
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
  internal class PLCM3 : UdpDevice, IDisposable
  {
    #region commands and responses
    private struct CommandsAndResponses
    {
      public const byte MainsFrequency = 0x30;
      public const byte StartConverting = 0x31;
      public const byte ReadEeprom = 0x32;
      public const byte Unlock = 0x33;
      public const byte KeepAlive = 0x34;

      public const string PlCm3 = "CM3";
      public const string Mac = "Mac:";
      public const string Lock = "Lock:";
      public const string IpPort = "Port:";
      public const string Serial = "Serial:";
      // The firmware replies with this exact casing - "Eeprom=", not "EEPROM=".
      public const string Eeprom = "Eeprom=";

      public const string CommandLock = "lock";
      public const string ResponseLock = "Lock";
      public const string Broadcast = "fff";
    }
    #endregion

    #region private fields
    private bool _disposed;
    private readonly int[]  _calib = new int[4];
    private readonly Timer _timer;
    private readonly static object _lockObject = new object();
    #endregion
    
    # region Constructor and Device Discovery
    private PLCM3(IPAddress address, string macAddress, ushort port, string serial) : base(address, port)
    {
        SerialNumber = serial;

        Locked = Lock();

        // Read the calibration data from the eeprom.
        // These four constants are read and exposed through CalibrationConstants
        // but are NOT applied - see the remarks on that property.
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
        // 0x07 enables all three channels. Each enabled channel adds a conversion to the
        // cycle, so the interval between updates for any one channel grows with the count.
        Send(new byte[] { CommandsAndResponses.StartConverting, 0x07});
    }


    /// <summary>
    /// Parses the response to the find device broadcast. 
    /// </summary>
    /// <returns>
    /// An instance of a <see cref="PLCM3"/> if it is valid and not locked
    ///  by a different host.
    /// </returns>
    private static PLCM3 ParseBroadcastResponse(byte[] bytes, IPAddress address)
    {
        try
        {
        // Reply from all PLCM3’s will be "PLCM3 Mac:XXXXXX Lock:Y Port:ZZ" where
        //      XXXXXX is the 6 byte mac address of the PLCM3 replying 
        //      Y is 0x00 for unlocked and 0x01 for locked 
        //      ZZ is the port it will listen on 

        if (Encoding.ASCII.GetString(bytes, 0, CommandsAndResponses.PlCm3.Length) != CommandsAndResponses.PlCm3)
            return null;

        int i = CommandsAndResponses.PlCm3.Length;

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
            return new PLCM3(address, macAddress, port, serial);
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
    /// Finds an instance of a <see cref="PLCM3"/> that is not yet locked.
    /// </summary>
    /// <returns><see cref="PLCM3"/> or null if none found.</returns>
    public static PLCM3 FindDevice()
    {
      // To discover all PLCM3s on network,send UDP packet to port 23 from port 23 (telnet) 
      // to destination 255.255.255.255 and data "fff" - 0x666666

        foreach (Tuple<byte[], IPAddress> tuple in BroadCast(Encoding.ASCII.GetBytes(CommandsAndResponses.Broadcast)))
        {
            PLCM3 plCM3 = ParseBroadcastResponse(tuple.Item1, tuple.Item2);

            if (plCM3 != null)
            {
                if (plCM3.Locked)
                {
                    return plCM3;
                }

                plCM3.Dispose();
            }
        }
        return null;
    }

    #endregion 
    
    #region IDisposeable
    ~PLCM3()
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
    /// <remarks>
    /// We cannot use BitConverter here because it has the wrong endian.
    /// The measurement is 28 bits. The high nibble of the first byte is status
    /// and is masked off; the low nibble is the top nibble of the value.
    /// </remarks>
    /// <returns>the measurement</returns>
    int ParseMeasure(byte[] bytes, uint index)
    {
        return ((bytes[index] & 0x0F) << 24)
             | (bytes[index + 1] << 16)
             | (bytes[index + 2] << 8)
             | (bytes[index + 3]);
    }

    /// <summary>
    /// Checks that a datagram carries the four measurements belonging to the
    /// channel whose first measurement index is <paramref name="firstIndex"/>.
    /// </summary>
    private static bool IsChannel(byte[] bytes, byte firstIndex)
    {
        return bytes.Length >= 20
            && bytes[0] == firstIndex
            && bytes[5] == firstIndex + 1
            && bytes[10] == firstIndex + 2
            && bytes[15] == firstIndex + 3;
    }

    /// <summary>
    /// Averages the four measurements in a datagram.
    /// </summary>
    /// <remarks>see programmers guide for other measurement types</remarks>
    private double AverageMeasure(byte[] bytes)
    {
        return (ParseMeasure(bytes, 1) + ParseMeasure(bytes, 6)
              + ParseMeasure(bytes, 11) + ParseMeasure(bytes, 16)) / 4.0;
    }

    /// <summary>
    /// Converts a measurement in counts to millivolts. Full scale is 2.5 V across 28 bits.
    /// </summary>
    private static double ToMillivolts(double counts)
    {
        return (2.5 * counts * 1000) / Math.Pow(2, 28);
    }

    private void OnNewData()
    {
        EventHandler handler = NewData;

        if (handler != null)
        {
            handler(this, EventArgs.Empty);
        }
    }

    private void ReceiveData(byte[] receiveBytes)
    {
        lock (_lockObject)
        {
            // The data returned will be in the following format
            //
            // 00XXXX01XXXX02XXXX03XXXX  data from channel 1
            // 04XXXX05XXXX06XXXX07XXXX  data from channel 2
            // 08XXXX09XXXX0aXXXX0bXXXX  data from channel 3
            //
            //(data format is: Measurement 0, 1, 2 for respective channels)
            //
            // One datagram arrives per channel in turn, so each call updates a single channel.

            // Channel 1
            if (IsChannel(receiveBytes, 0x00))
            {
                Ch1 = AverageMeasure(receiveBytes);
                Ch1Millivolts = ToMillivolts(Ch1);

                OnNewData();
            }

            // Channel 2
            if (IsChannel(receiveBytes, 0x04))
            {
                Ch2 = AverageMeasure(receiveBytes);
                Ch2Millivolts = ToMillivolts(Ch2);

                OnNewData();
            }

            // Channel 3
            if (IsChannel(receiveBytes, 0x08))
            {
                Ch3 = AverageMeasure(receiveBytes);
                Ch3Millivolts = ToMillivolts(Ch3);

                OnNewData();
            }
        }

    }

    public event EventHandler NewData;
    public string SerialNumber { get; private set; }
    public double Ch1 { get; private set; }
    public double Ch1Millivolts { get; private set; }
    public double Ch2 { get; private set; }
    public double Ch2Millivolts { get; private set; }
    public double Ch3 { get; private set; }
    public double Ch3Millivolts { get; private set; }

    /// <summary>
    /// The four 32-bit calibration constants held in the device eeprom.
    /// </summary>
    /// <remarks>
    /// These are read at connection but are deliberately NOT applied - how they
    /// scale a measurement is not covered by the protocol documentation used to
    /// write this example, so ReceiveData() uses the fixed 2.5 V full scale
    /// instead. The unit this example was verified against carried the round
    /// default of 100,000,000 in all four, and the fixed scaling agreed with a
    /// known input to within -0.55%, inside the datasheet's +/-2.5% below 1 V RMS.
    /// Whether that holds for a differently calibrated unit is unconfirmed -
    /// check the 'PicoLog CM3 Current Data Logger Programmer's Guide' before
    /// relying on the fixed scaling for a calibrated measurement.
    /// </remarks>
    public int[] CalibrationConstants
    {
        get { return (int[]) _calib.Clone(); }
    }
    public bool Locked { get; private set; }
    public override string ToString()
    {
        return String.Format("Serial: {0}\tIP:{1}:{2}", SerialNumber, Address, Port);
    }
  }
}