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
using System.Net;
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
   
    public static IEnumerable<Tuple<byte[], IPAddress>> BroadCast(byte[] sendData)
    {
      List<Tuple<byte[], IPAddress>> result = new List<Tuple<byte[], IPAddress>>();
      using (UdpClient client = new UdpClient())
      {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 23);

        client.Client.Bind(ep);

        client.Send(sendData, sendData.Length, new IPEndPoint(IPAddress.Broadcast, 23));

        Thread.Sleep(500);
        while (client.Available > 0)
        {
          IPEndPoint ep2 = ep;
          result.Add(new Tuple<byte[], IPAddress>(client.Receive(ref ep2), ep2.Address));
        }
        client.Close();
      }
      return result;
    }
  }
}