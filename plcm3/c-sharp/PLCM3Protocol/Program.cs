/*******************************************************************************
 *
 * Filename: Program.cs
 *
 * Description:
 *   This is a console-mode program that demonstrates how to use the ethernet
 *   protocol commands to retrieve data from a PicoLog CM3 Current Data Logger.
 *    
 * Copyright (C) 2017 Pico Technology Ltd. See LICENSE file for terms.    
 *    
 *******************************************************************************/
using System;

namespace PLCM3Protocol
{
    class Program
    {
        static void Main()
        {
            using(PLCM3 picologCM3 = PLCM3.FindDevice())
            {
                if (picologCM3 != null)
                {
                    Console.Out.WriteLine("Found Device: {0}", picologCM3);
                    Console.Out.WriteLine();

                    picologCM3.NewData +=
                        delegate(object sender, EventArgs args)
                    {
                        PLCM3 p = (PLCM3) sender;
                        Console.Out.WriteLine("{0}:\tCh1 {1}\t{2} mV\tCh2 {3}\t{4} mV", p.SerialNumber, p.Ch1, p.Ch1Millivolts, p.Ch2, p.Ch2Millivolts);
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
