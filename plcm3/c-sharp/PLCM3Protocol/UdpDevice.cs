/*******************************************************************************
 *
 * Filename: UdpDevice.cs
 *
 * Description:
 *   Encapsulates a UDP port
 *    
 * Copyright (C) 2013 - 2026 Pico Technology Ltd. See LICENSE file for terms.    
 *    
 *******************************************************************************/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace PLCM3Protocol
{
  /// <summary>
  /// Encapsulates a UDP port
  /// </summary>
  internal class UdpDevice 
  {
    public IPAddress Address { get; private set; }
    public ushort Port { get; private set; }

    private readonly UdpClient _udpClient = new UdpClient();
    private IPEndPoint _endPoint;

    protected UdpDevice(IPAddress address, ushort port)
    {
      Port = port;
      Address = address;

      // Initialise the UDP Client at the correct address
      _endPoint = new IPEndPoint(address, port);
      _udpClient.Connect(_endPoint);
    }
    protected void Close()
    {
      _udpClient.Close();
    }
    protected void Send(byte[] data)
    {
      int duff = _udpClient.Send(data, data.Length);
    }
    protected byte[] Receive()
    {
      IPEndPoint ipEndPoint = _endPoint;
      return _udpClient.Receive(ref ipEndPoint);
    }

    protected void BeginReceive(Action<byte[]> receiveData)
    {
      _udpClient.BeginReceive(ReceiveData, receiveData);
    }
    private void ReceiveData(IAsyncResult ar)
    {
      try
      {
        Action<byte[]> action = ((Action<byte[]>) ar.AsyncState);

        Byte[] receiveBytes = _udpClient.EndReceive(ar, ref _endPoint);
        _udpClient.BeginReceive(ReceiveData, action);
        action.Invoke(receiveBytes);
      }
      catch(ObjectDisposedException)
      {
      }
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
    /// Broadcasts <paramref name="sendData"/> and collects the replies.
    /// </summary>
    /// <remarks>
    /// The socket is bound to each local interface in turn rather than to
    /// IPAddress.Any. A broadcast from the wildcard address leaves by the
    /// default-route interface only, so on a multi-homed host a device on any
    /// other subnet would never be found.
    /// </remarks>
    public static IEnumerable<Tuple<byte[], IPAddress>> BroadCast(byte[] sendData)
    {
      List<Tuple<byte[], IPAddress>> result = new List<Tuple<byte[], IPAddress>>();
      HashSet<string> alreadySeen = new HashSet<string>();

      foreach (IPAddress localAddress in LocalIPv4Addresses())
      {
        using (UdpClient client = new UdpClient())
        {
          IPEndPoint ep = new IPEndPoint(localAddress, 23);

          try
          {
            client.Client.Bind(ep);
            client.EnableBroadcast = true;

            client.Send(sendData, sendData.Length, new IPEndPoint(IPAddress.Broadcast, 23));

            Thread.Sleep(500);
            while (client.Available > 0)
            {
              IPEndPoint ep2 = ep;
              byte[] received = client.Receive(ref ep2);

              // A device reachable from more than one interface answers more than once.
              if (alreadySeen.Add(ep2.Address.ToString()))
                result.Add(new Tuple<byte[], IPAddress>(received, ep2.Address));
            }
          }
          catch (SocketException)
          {
            // This interface cannot be used for the broadcast - try the next one.
          }

          client.Close();
        }
      }
      return result;
    }
  }
}