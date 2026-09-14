/*******************************************************************************
 *
 * Filename: Program.cs
 *
 * Description:
 *   This is a console-mode program that demonstrates how to use the ethernet
 *   protocol commands to retrieve data from a PicoLog CM3 Current Data Logger.
 *    
 * Copyright (C) 2017 - 2026 Pico Technology Ltd. See LICENSE file for terms.    
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
                        Console.Out.WriteLine("{0}:\tCh1 {1:F1}\t{2:F3} mV\tCh2 {3:F1}\t{4:F3} mV\tCh3 {5:F1}\t{6:F3} mV",
                            p.SerialNumber,
                            p.Ch1, p.Ch1Millivolts,
                            p.Ch2, p.Ch2Millivolts,
                            p.Ch3, p.Ch3Millivolts);
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
