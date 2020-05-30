# About this Repo

This is a fork of the Machina library to run on Linux under .NET Core 3.1. Not all features work properly (Neither of the original socket types work, needed to create a third one, firewall control does not work). This likely does not work on Windows, but there are plans to make sure it will work on both.

Additional Dependencies from upstream:

- `lsof` is required to get connections for a process, vs the win32 API used upstream
- The NuGet packages SnmpShartNet and PacketDotNet have been added to make the packet capture easier (and ideally cross platform)

# Machina

Machina is a library that allows developers to read network data from the windows networking subsystem and reassemble it into usable information.

It supports the following features:
* Simple raw socket for data capture or optional WinPcap driver support
* IP Fragmentation reassembly
* TCP stream reassembly, including retransmits

Because it is accessing network data, it does require running under elevated security privleges on the local machine.  It also requires configuring access through the local firewall, or disabling it completely, in order to read data.

In order to simplify use of this library, the TCPNetworkMonitor class was added to poll the network data for a specific process and raise an event when new data arrives.  Use of this class can be found in the TCPNetworkMonitorTests class, but here is some sample code:


    public static void Main(string[] args)
    {
        TCPNetworkMonitor monitor = new TCPNetworkMonitor();
        monitor.WindowName = "FINAL FANTASY XIV";
        monitor.MonitorType = TCPNetworkMonitor.NetworkMonitorType.RawSocket;
        monitor.DataReceived = (string connection, byte[] data) => DataReceived(connection, data);
        monitor.Start();
        // Run for 10 seconds
        System.Threading.Thread.Sleep(10000);
        monitor.Stop();
    }
    private static void DataReceived(string connection, byte[] data)
    {
        // Process Data
    }

The import elements in the above code are:
1) Configure the monitor class with the correct window name or process ID
2) Hook the monitor up to a data received event
3) Start the monitor - this kicks off a long-running Task
4) Process the data in the DataReceived() event handler
5) Stop the monitor before exiting the process, to prevent unmanaged resources from leaking.  This mostly affects WinPCap.

Prior to the above, be sure to either disable windows firewall, or add a rule for any exceutable using the above code to work through it.  And, the code must be executed as a local administrator.  To debug the above code, you will need to start Visual Studio using the 'Run as Administrator' option in Windows.

The public property UseSocketFilter, when set to true, will apply socket and winpcap filters on both source and target IP Addresses for the connections being monitored.  Note that this means that each connection to a new remote IP must be detected and listener started before data will be received.  It is likely that some network data will be lost between when the process initiates the connection, and when the Machina library begins to listen.  It should only be used if the initial data sent on the connection is not critical.  However, it has the benefit of significantly reducing the potential for data loss when there is excessive local network traffic.

# Machina.FFXIV
Machina.FFXIV is an extension to the Machina library that decodes Final Fantasy XIV network data and makes it available to programs.  It uses the Machina library to locate the game traffic and decode the TCP/IP layer, and then decodes / decompresses the game data into individual game messages.  It processes both incoming and outgoing messages.

    public static void Main(string[] args)
    {
        FFXIVNetworkMonitor monitor = new FFXIVNetworkMonitor();
        monitor.MessageReceived = (string connection, long epoch, byte[] message) => MessageReceived(connection, epoch, message);
        monitor.Start();
        // Run for 10 seconds
        System.Threading.Thread.Sleep(10000);
        monitor.Stop();
    }
    private static void MessageReceived(string connection, long epoch, byte[] message)
    {
        // Process Message
    }

An optional Process ID and network monitor type can be specified as properties, to configure per the end-user's machine requirements.

An optional property UseSocketFilter can be set, which is passed through to the TCPNetworkMonitor's property with the same name.  This is generally fine for FFXIV, since the TCP connection does not frequently change.
