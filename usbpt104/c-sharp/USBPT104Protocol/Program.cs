/*******************************************************************************
 *
 * Filename: Program.cs
 *
 * Description:
 *   This is a console-mode program that demonstrates how to use the ethernet
 *   protocol commands to retrieve data from a USB PT-104 Platinum Resistance
 *   Temperature Data Logger.
 *    
 * Copyright (C) 2013 - 2017 Pico Technology Ltd. See LICENSE file for terms.    
 *    
 *******************************************************************************/
using System;

namespace USBPT104Protocol
{
	class Program
	{
		static void Main()
		{
			using (UdpPt104 pt104 = UdpPt104.FindDevice())
			{
				if (pt104 != null)
				{
					// Enabled channels 1 and 2, set frequency rejection to 50Hz.
					pt104.InitConfigure(0x03, 0x00);

					Console.Out.WriteLine("Found Device: {0}", pt104);

					pt104.NewData +=
					  delegate (Object sender, EventArgs args)
						{
							UdpPt104 p = (UdpPt104)sender;
							for (Int32 i = 0; i < p.Ch.Length; i++)
							{
								Console.Out.WriteLine("{0}:\tCh{1} {2}", p.SerialNumber, i, p.Ch[i]);
							}
						};
				}
				else
				{
					Console.Out.WriteLine("No Devices Found");
				}

				Console.ReadKey();
			}
		}
	}
}
