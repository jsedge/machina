using System;
using System.Linq;
using System.Net;
using System.Threading;
using SharpPcap;

namespace Machina {
    public class RawSharpPcap : IRawSocket
    {
        public uint LocalIP
        { get; private set; }
        public uint RemoteIP
        { get; private set; }

        private ICaptureDevice CaptureDevice;

        private NetworkBufferFactory _bufferFactory = new NetworkBufferFactory(20, 0);

        public void Create(uint localAddress, uint remoteAddress = 0){
            LocalIP = localAddress;
            RemoteIP = remoteAddress;
            var devices = CaptureDeviceList.Instance;
            CaptureDevice = devices.First();
            CaptureDevice.OnPacketArrival += new PacketArrivalEventHandler(ProcessPcapData);
            CaptureDevice.Open(DeviceMode.Normal, 1000);

            string filterText = "ip and tcp";
            if (remoteAddress > 0)
                filterText += " and host " + new IPAddress(remoteAddress).ToString();
            Console.WriteLine($"Filter: {filterText}");
            CaptureDevice.Filter = filterText;
            var thread = new Thread(CaptureDevice.Capture);
            thread.Start();
            Console.WriteLine("Capture device is capturing");
        }

        public int Receive(out byte[] buffer)
        {
            // retrieve data from allocated buffer.
            NetworkBufferFactory.Buffer data = _bufferFactory.GetNextAllocatedBuffer();
            buffer = data?.Data;
            // Console.WriteLine($"Returning {data?.AllocatedSize} bytes");
            return data?.AllocatedSize ?? 0;
        }

        public void FreeBuffer(ref byte[] buffer)
        {
            NetworkBufferFactory.Buffer data = new NetworkBufferFactory.Buffer() { Data = buffer, AllocatedSize = 0 };
            _bufferFactory.AddFreeBuffer(data);
        }

        public void Destroy(){
            Console.WriteLine("Destroying Pcap device");
            CaptureDevice.Close();
        }

        private void ProcessPcapData(object sender, CaptureEventArgs e){
            NetworkBufferFactory.Buffer buffer = _bufferFactory.GetNextFreeBuffer();

            // prepare data - skip the 14-byte ethernet header
            buffer.AllocatedSize = (int)e.Packet.Data.Length;
            if (buffer.AllocatedSize > buffer.Data.Length)
                Console.WriteLine("RawPCap: packet length too large: " + buffer.AllocatedSize.ToString(), "DEBUG-MACHINA");
            else
            {
                for(int i = 0; i < buffer.AllocatedSize; i++){
                    buffer.Data[i] = e.Packet.Data[i];
                }
                
                _bufferFactory.AddAllocatedBuffer(buffer);
            }
        }
    }
}