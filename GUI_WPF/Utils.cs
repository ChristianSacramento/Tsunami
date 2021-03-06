﻿using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Tsunami.Gui.Wpf
{
    /// <summary>
    /// The pourpouse of this Class is to Create a dll full with general porpouse functions 
    /// to use ad includes in vaious projects.
    /// </summary>
    public static class Utils
    {
        [DllImport("Shlwapi.dll", CharSet = CharSet.Auto)]
        public static extern long StrFormatByteSize(long fileSize, System.Text.StringBuilder buffer, int bufferSize);

        public static bool IsWindowsOs()
        {
            if (Environment.OSVersion.Platform == PlatformID.MacOSX && Environment.OSVersion.Platform == PlatformID.Unix)
            {
                return false;
            }
            else return true;
        }

        public static bool Is64BitOs()
        {
            //if (IntPtr.Size == 8)       // 64 bit - impostare il vs. path
            //    return true;
            //else if (IntPtr.Size == 4)  // 32 bit - impostare il vs. path
            //    return false;
            if (Environment.Is64BitOperatingSystem)
            {
                return true;
            }
            else return false;
        }

        public static string GetWinVlcPath()
        {
            RegistryKey localMachine = Registry.LocalMachine;
            string InstallPath = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\VideoLAN\VLC", "InstallDir", null);
            if (InstallPath != null)
            {
                return InstallPath; ;
            }
            else throw new Exception("VLC non trovato nel registro"+InstallPath);
        }

        /// <summary>
        /// Converts a numeric value into a string that represents the number expressed as a size value in bytes, kilobytes, megabytes, or gigabytes, depending on the size.
        /// Source: http://www.pinvoke.net/default.aspx/shlwapi.strformatbytesize
        /// </summary>
        /// <param name="filelength">The numeric value to be converted.</param>
        /// <returns>the converted string</returns>
        public static string StrFormatByteSize(long filesize)
        {
            StringBuilder sb = new StringBuilder(11);
            StrFormatByteSize(filesize, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
