using NModbus;
using NModbus.Serial;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Ports;
using System.Windows.Forms;

namespace HMIProject
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }

    }
}