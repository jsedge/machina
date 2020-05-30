// Machina ~ ProcessTCPInfo.cs
// 
// Copyright © 2017 Ravahn - All Rights Reserved
// 
//This program is free software: you can redistribute it and/or modify
//it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or
//(at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Linq;

namespace Machina
{
    /// <summary>
    /// Manages access to the TCP table and assists with tracking when the connections change per-process
    /// </summary>
    public unsafe class ProcessTCPInfo
    {

        

       
        private const Int32 AF_INET = 2;


        /// <summary>
        /// Process ID of the process to return network connection information about
        /// </summary>
        public uint ProcessID
        { get; set; } = 0;

        /// <summary>
        /// Window text of the process to return network connection information about
        /// </summary>
        public string ProcessWindowName
        { get; set; } = "";
        
        /// <summary>
        /// Window class of the process to return network connection information about
        /// </summary>
        public string ProcessWindowClass
        { get; set; } = "";


        private uint _currentProcessID = 0;

        /// <summary>
        /// This returns the process id of the first window with the specified window name.
        /// </summary>
        /// <param name="windowName">name of the window to look for</param> 
        /// <returns>Process ID</returns>
        // public uint GetProcessIDByWindowName(string windowName)
        // {
        //     uint processID;
        //     IntPtr hWindow = FindWindow(null, windowName);
        //     GetWindowThreadProcessId(hWindow, out processID);

        //     return processID;
        // }
        
        /// <summary>
        /// This returns the process id of the first window with the specified window class.
        /// </summary>
        /// <param name="windowClass">class of the window to look for</param> 
        /// <returns>Process ID</returns>
        // public uint GetProcessIDByWindowClass(string windowClass)
        // {
        //     uint processID;
        //     IntPtr hWindow = FindWindowEx(IntPtr.Zero, IntPtr.Zero, windowClass, null);
        //     GetWindowThreadProcessId(hWindow, out processID);

        //     return processID;
        // }

        public List<TCPConnection> GetTCPConnections(){
            List<TCPConnection> tcpConnections = new List<TCPConnection>();
            var proc = new Process{
                StartInfo = new ProcessStartInfo() {
                FileName = "lsof",
                Arguments = $"-ai -p {ProcessID}",
                RedirectStandardOutput = true
            }};

            proc.Start();

            List<string> output = new List<string>();

            while(!proc.StandardOutput.EndOfStream){
                output.Add(proc.StandardOutput.ReadLine());
            }
            foreach(var line in output.Skip(1)){
                // There's probably a much cleaner way to do this
                // Parsing the following format source.ip:source.port->remote.ip:remote.port
                var connectionInfo = line.Split(' ').Where(x => x.Contains("->")).First().Split("->");
                var localIp = connectionInfo.First().Split(':').First();
                var localPort = connectionInfo.First().Split(':').Last();
                var remoteIp = connectionInfo.Last().Split(':').First();
                var remotePort = connectionInfo.Last().Split(':').Last();

                //Console.WriteLine($"Local IP: {localIp}, Local Port: {localPort}, Remote IP: {remoteIp}, Remote Port: {remotePort}");
                
                // lsof gives hostname instead of local ip, but that also seems to differ from Dns.GetHostname() for me
                // So just setting it to 127.0.0.1 works
                uint localIpUint = BitConverter.ToUInt32(IPAddress.Parse("127.0.0.1").GetAddressBytes(), 0);
                Console.WriteLine($"Using local ip {localIp}");
                tcpConnections.Add(new TCPConnection() {
                    LocalIP = localIpUint,
                    LocalPort = ushort.Parse(localPort),
                    RemoteIP = BitConverter.ToUInt32(IPAddress.Parse(remoteIp).GetAddressBytes(), 0),
                    RemotePort = ushort.Parse(remotePort)
                });
            }

            return tcpConnections;
        }

        /// <summary>
        /// This retrieves all current TCPIP connections, filters them based on a process id (specified by either ProcessID, ProcessWindowName or ProcessWindowClass parameter),
        ///   and updates the connections collection.
        /// </summary>
        /// <param name="connections">List containing prior connections that needs to be maintained</param>
        public unsafe void UpdateTCPIPConnections(List<TCPConnection> connections)
        {
            if (ProcessID > 0)
                ProcessID = ProcessID;
            else
                ProcessID = (uint)Process.GetProcessesByName(ProcessWindowName).First().Id;         

            if (_currentProcessID == 0)
            {
                if (connections.Count > 0)
                {
                    Trace.WriteLine("ProcessTCPInfo: Process has exited, closing all connections.", "DEBUG-MACHINA");

                    connections.Clear();
                }

                return;
            }

            try
            {
                var tcpConnections = GetTCPConnections();

                foreach (var conn in tcpConnections)
                {
                    bool bFound = false;
                    if(connections.Any(x => x.Equals(conn))){
                        Console.WriteLine("Found an existing connection");
                        bFound = true;
                        break;
                    }
                    
                    if (!bFound)
                    {
                        connections.Add(conn);
                        Console.WriteLine("ProcessTCPInfo: New connection detected for Process [" + _currentProcessID.ToString() + "]: " + conn.ToString(), "DEBUG-MACHINA");
                    }
                }

                foreach (var conn in connections)
                {
                    
                    if(!tcpConnections.Any(x => x.Equals(conn)))
                    {
                        Console.WriteLine("ProcessTCPInfo: Removed connection " + conn.ToString(), "DEBUG-MACHINA");
                        connections.Remove(conn);
                    }
                }
            
            }
            catch (Exception ex)
            {
                Trace.WriteLine("ProcessTCPInfo: Exception updating TCP connection list." + ex.ToString(), "DEBUG-MACHINA");
                throw;
            }
        }
    }
}
