﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PacketDotNet;

namespace Machina
{
    /// <summary>
    /// TCPNetworkMonitor is configured through the following properties after it is constructed:
    ///   MonitorType: Specifies whether it should use a winsock raw socket, or use WinPCap (requires separate kernel driver installation).  Default is a raw socket.
    ///   ProcessID: Specifies the process ID to record traffic from
    ///   WindowName: Specifies the window name to record traffic from, where process ID is unavailable
    ///   WindowClass: Specifies the window class to record traffic from, where process ID is unavailable
    ///   DataReceived: Delegate that is called when data is received and successfully decoded through IP and TCP decoders.  Note that a connection identifier is 
    ///     supplied to distinguish between multiple connections from the same process.
    ///   DataSent: Delegate that is called when data is sent and successfully decoded through IP and TCP decoders.  Note that a connection identifier is 
    ///     supplied to distinguish between multiple connections from the same process.
    ///   UseSocketFilter: boolean that specifies whether to start data capture as connections are detected within the target process (new behavior), or monitor
    ///     the primary interface for the process and capture all data sent/received on that interface - and filter it.  The new behavior may cause some data to be lost 
    ///     on connection startup, but significantly reduces the filtering overhead caused by other traffic on the network interface.
    ///     
    /// This class uses a long-running task to monitor the network data as it is received.  It also monitors the specified process for changes to its active
    ///   TCPIP connections and filters out all traffic not related to these connections
    ///   
    /// The stream of data sent to the remote server is considered separate from the stream of data sent by the remote server, and processed through different events.
    /// </summary>
    public class TCPNetworkMonitor
    {
        public enum NetworkMonitorType
        {
            RawSocket = 1,
            WinPCap = 2
        }

        /// <summary>
        /// Specifies the type of monitor to use - Raw socket or WinPCap
        /// </summary>
        public NetworkMonitorType MonitorType
        { get; set; } = NetworkMonitorType.RawSocket;

        /// <summary>
        /// Specifies the Process ID that is generating or receiving the traffic.  Either ProcessID, WindowName or WindowClass must be specified.
        /// </summary>
        public uint ProcessID
        { get; set; } = 0;

        /// <summary>
        /// Specifies the local IP address of the network interface to monitor
        /// </summary>
        public string LocalIP
        { get; set; } = "";

        /// <summary>
        /// Specifies the Window Name of the application that is generating or receiving the traffic.  Either ProcessID, WindowName or WindowClass must be specified.
        /// </summary>
        public string WindowName
        { get; set; } = "";

        /// <summary>
        /// Specifies the Window Class of the application that is generating or receiving the traffic.  Either ProcessID, WindowName or WindowClass must be specified.
        /// </summary>
        public string WindowClass
        { get; set; } = "";

        public bool UseSocketFilter
        { get; set; } = false;

        #region Data Delegates section
        public delegate void DataReceivedDelegate(string connection, byte[] data);

        /// <summary>
        /// Specifies the delegate that is called when data is received and successfully decoded/
        /// </summary>
        public DataReceivedDelegate DataReceived = null;

        public void OnDataReceived(string connection, byte[] data)
        {
            DataReceived?.Invoke(connection, data);
        }

        public delegate void DataSentDelegate(string connection, byte[] data);

        public DataSentDelegate DataSent = null;

        public void OnDataSent(string connection, byte[] data)
        {
            DataSent?.Invoke(connection, data);
        }

        #endregion

        private List<IRawSocket> _sockets = new List<IRawSocket>();
        private List<TCPConnection> _connections = new List<TCPConnection>(2);


        private Thread _monitorThread = null;
        private int _Abort = 0;
        private bool Abort
        {
            get
            {
                return _Abort != 0;
            }
            set
            {
                if (value)
                    Interlocked.Exchange(ref _Abort, 1);
                else
                    Interlocked.Exchange(ref _Abort, 0);
            }
        }

        private ProcessTCPInfo _processTCPInfo = new ProcessTCPInfo();

        /// <summary>
        /// Validates the parameters and starts the monitor.
        /// </summary>
        public void Start()
        {
            UseSocketFilter = true;
            if (ProcessID == 0 && string.IsNullOrWhiteSpace(WindowName) && string.IsNullOrWhiteSpace(WindowClass))
                throw new ArgumentException("Either Process ID, Window Name or Window Class must be specified");
            if (DataReceived == null)
                throw new ArgumentException("DataReceived delegate must be specified.");

            _processTCPInfo.ProcessWindowName = WindowName;
            _processTCPInfo.ProcessWindowName = "ffxiv_dx11.exe";
            _processTCPInfo.ProcessWindowClass = WindowClass;

            _monitorThread = new Thread(new ParameterizedThreadStart(Run));
            _monitorThread.Name = "Machina.TCPNetworkMonitor.Start";
            _monitorThread.IsBackground = true;
            _monitorThread.Start(this);
            Console.WriteLine("TCPNetworkMonitor has started");
        }

        /// <summary>
        /// Stops the monitor if it is active.
        /// </summary>
        public void Stop()
        {
            Abort = true;
            if (_monitorThread != null)
            {
                if (_monitorThread.IsAlive)
                {
                    for (int i = 0; i < 50; i++)
                        if (_monitorThread.IsAlive)
                            Thread.Sleep(10);
                        else
                            break;
                }

                try
                {
                    if (_monitorThread.IsAlive)
                        _monitorThread.Abort();
                }
                catch (Exception)
                {
                }

                _monitorThread = null;
            }

            Cleanup();
            Abort = false;
        }

        private void Cleanup()
        {
            for (int i = 0; i < _sockets.Count; i++)
            {
                _sockets[i].Destroy();
                Trace.WriteLine("TCPNetworkMonitor: Stopping " + MonitorType.ToString() + " listener between [" +
                    new IPAddress(_sockets[i].LocalIP).ToString() + "] => [" +
                    new IPAddress(_sockets[i].RemoteIP).ToString() + "].", "DEBUG-MACHINA");
            }

            _sockets.Clear();
            _connections.Clear();
        }

        private void Run(object state)
        {
            while (!(state as TCPNetworkMonitor).Abort)
            {
                try
                {
                    UpdateProcessConnections();
                    if (_connections.Count == 0)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    UpdateSockets();

                    ProcessNetworkData();


                    Thread.Sleep(30);
                }
                catch (ThreadAbortException)
                {
                    // do nothing, thread is aborting.
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("TCPNetworkMonitor Error: " + ex.ToString(), "DEBUG-MACHINA");
                    return;
                }
            }

            Cleanup();
        }

        private void UpdateProcessConnections()
        {
            // get any active game connections
            _processTCPInfo.UpdateTCPIPConnections(_connections);
            if (_connections.Count == 0)
            {
                Cleanup();
                return;
            }

            for (int i = 0; i < _connections.Count; i++)
            {
                TCPConnection connection = _connections[i];
                if (string.IsNullOrWhiteSpace(connection.ID))
                {
                    connection.ID = connection.ToString(); //TODO: In the future there may be a better way to define the ID other than such a long string.

                    // Set up decoders for data sent from local machine
                    connection.IPDecoder_Send = new IPDecoder(connection.LocalIP, connection.RemoteIP, IPProtocol.TCP);
                    connection.TCPDecoder_Send = new TCPDecoder(connection.LocalPort, connection.RemotePort);

                    // set up decoders for data received by local machine
                    connection.IPDecoder_Receive = new IPDecoder(connection.RemoteIP, connection.LocalIP, IPProtocol.TCP);
                    connection.TCPDecoder_Receive = new TCPDecoder(connection.RemotePort, connection.LocalPort);

                    continue;
                }
            }
        }

        private void UpdateSockets()
        {
            for (int i = 0; i < _connections.Count; i++)
            {
                bool found = false;
                for (int j = 0; j < _sockets.Count; j++)
                    if (_connections[i].LocalIP == _sockets[j].LocalIP &&
                        (!UseSocketFilter || (_connections[i].RemoteIP == _sockets[j].RemoteIP)))
                        found = true;

                if (!found)
                {
                    Trace.WriteLine("TCPNetworkMonitor: Starting " + MonitorType.ToString() + " listener on [" +
                        new IPAddress(_connections[i].LocalIP).ToString() + "]" +
                        (UseSocketFilter ? "=> [" + new IPAddress(_connections[i].RemoteIP).ToString() + "]." : ""), "DEBUG-MACHINA");

                    // if (MonitorType == NetworkMonitorType.WinPCap)
                    //     _sockets.Add(new RawPCap());
                    // else if (MonitorType == NetworkMonitorType.RawSocket)
                    //     _sockets.Add(new RawSocket());
                    // else
                    _sockets.Add(new RawSharpPcap());
                    _sockets.Last().Create(_connections[i].LocalIP, UseSocketFilter ? _connections[i].RemoteIP : 0);
                }
            }

            for (int i = _sockets.Count - 1; i >= 0; i--)
            {
                bool found = false;
                for (int j = 0; j < _connections.Count; j++)
                    if (_connections[j].LocalIP == _sockets[i].LocalIP &&
                        (!UseSocketFilter || (_connections[j].RemoteIP == _sockets[i].RemoteIP)))
                        found = true;

                if (!found)
                {
                    Console.WriteLine("TCPNetworkMonitor: Stopping " + MonitorType.ToString() + " listener on [" +
                        new IPAddress(_sockets[i].LocalIP).ToString() + "]" +
                        (UseSocketFilter ? "=> [" + new IPAddress(_sockets[i].RemoteIP).ToString() + "]." : ""), "DEBUG-MACHINA");
                    _sockets[i].Destroy();
                    _sockets.RemoveAt(i);
                }
            }

        }

        private void ProcessNetworkData()
        {
            int size;
            byte[] buffer;

            for (int i = 0; i < _sockets.Count; i++)
            {
                while ((size = _sockets[i].Receive(out buffer)) > 0)
                {
                    ProcessData(buffer, size);
                    _sockets[i].FreeBuffer(ref buffer);
                }
            }
        }


        private void ProcessData(byte[] buffer, int size)
        {
            byte[] tcpbuffer;
            byte[] payloadBuffer;
            foreach (var connection in _connections)
            {
                var packet = PacketDotNet.Packet.ParsePacket(LinkLayers.Ethernet, buffer);

                var tcpPacket = packet.Extract<PacketDotNet.TcpPacket>();
                var ipPacket = (PacketDotNet.IPPacket)tcpPacket.ParentPacket;
                
                if (new IPAddress(connection.RemoteIP).Equals(ipPacket.SourceAddress) &&
                    connection.RemotePort == tcpPacket.SourcePort)
                {
                    OnDataReceived(connection.ID, tcpPacket.PayloadData);
                }
                else if (new IPAddress(connection.RemoteIP).Equals(ipPacket.DestinationAddress) &&
                    connection.RemotePort == tcpPacket.DestinationPort)
                {
                    OnDataSent(connection.ID, tcpPacket.PayloadData);
                }

                continue; // Original Windows code below here
                connection.IPDecoder_Send.FilterAndStoreData(buffer, size);

                while ((tcpbuffer = connection.IPDecoder_Send.GetNextIPPayload()) != null)
                {
                    Console.WriteLine("In outer send loop");
                    connection.TCPDecoder_Send.FilterAndStoreData(tcpbuffer);
                    while ((payloadBuffer = connection.TCPDecoder_Send.GetNextTCPDatagram()) != null)
                    {
                        Console.WriteLine("In inner send loop");
                        Console.WriteLine(payloadBuffer);
                        OnDataSent(connection.ID, payloadBuffer);
                    }
                }

                connection.IPDecoder_Receive.FilterAndStoreData(buffer, size);
                while ((tcpbuffer = connection.IPDecoder_Receive.GetNextIPPayload()) != null)
                {
                    Console.WriteLine("In outer recieve loop");
                    connection.TCPDecoder_Receive.FilterAndStoreData(tcpbuffer);
                    while ((payloadBuffer = connection.TCPDecoder_Receive.GetNextTCPDatagram()) != null)
                    {
                        Console.WriteLine("In inner recieve loop");
                        OnDataReceived(connection.ID, payloadBuffer);
                    }
                }
            }
        }

    }
}
