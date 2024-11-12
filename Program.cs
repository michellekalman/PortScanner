using System;
using System.Collections.Generic;
using System.Threading.Channels;
using Microsoft.VisualBasic;
using System.Linq;
using System.Net;
using port_scanner;

namespace port_scanner
{
    
    public class ItemToScan
    {
        private readonly IPAddress ip_addr;
        private readonly int port;

        public ItemToScan(IPAddress ip_address, int port_number)
        {
            ip_addr = ip_address;
            port = port_number;
        }
        public IPAddress IP
        {
            get => ip_addr;
        }

        public int Port
        {
            get => port;
        }
    }
    public class IPRangeGenerator
    {
        public static IEnumerable<IPAddress> GetIPRange(IPAddress startIP, IPAddress endIP)
        {
            // Convert start and end IP addresses to integers
            uint start = BitConverter.ToUInt32(startIP.GetAddressBytes().Reverse().ToArray(), 0);
            uint end = BitConverter.ToUInt32(endIP.GetAddressBytes().Reverse().ToArray(), 0);

            // Iterate through the range and yield each IP address
            for (uint current = start; current <= end; current++)
            {
                byte[] bytes = BitConverter.GetBytes(current).Reverse().ToArray();
                yield return new IPAddress(bytes);
            }
        }
    }
    public class MissionSupplier
    {
        
        private readonly Channel<ItemToScan> channel;
        private readonly ChannelWriter<ItemToScan> writer;
        private IPAddress startIP;
        private IPAddress endIP;
        private int[] ports;

        public MissionSupplier(IPAddress startIPAddr, IPAddress endIPAddr, int[] portArray, Channel<ItemToScan> unboundChannel)
        {
            channel = unboundChannel;
            writer = channel.Writer;
            startIP = startIPAddr;
            endIP = endIPAddr;
            ports = portArray;
        }

        public void Run()
        {  
            List<IPAddress> ips = [.. IPRangeGenerator.GetIPRange(startIP, endIP)];

            // Generate all IP and port combinations
            var ipPortPairs = ips.SelectMany(
                ip => ports,
                (ip, port) => (IP: ip, Port: port)
            ).ToList();

            foreach (var (IP, Port) in ipPortPairs)
            {
                bool success = writer.TryWrite(new ItemToScan(IP, Port));
                Console.WriteLine(success
                    ? $"Message '{IP} {Port}' written to the channel."
                    : $"Failed to write {IP} {Port} to the channel.");
            } 
            
            
        }
            
    }

       
    
    public class Program
    {
        public static void Main(string[] args)
        {
            while(true)
            {
                Console.WriteLine("Enter start ip address, end ip address and ports");
                string? input = Console.ReadLine();

                if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine("No input received.");
                    break;
                }
                
                string[] words = input.Split(' ');
                if (words.Length != 3){
                    Console.WriteLine("Invalid input!");
                    break;
                }

                // Parse the start and end IP strings to IPAddress objects
                IPAddress startIP = IPAddress.Parse(words[0]);
                IPAddress endIP = IPAddress.Parse(words[1]);

                string[] str_ports = words[2].Split(',');
                int[] ports = str_ports
                .Select(s => int.TryParse(s, out int result) ? result : (int?)null)  // Use nullable int to handle failures
                .Where(n => n.HasValue)   // Filter out nulls (failed parses)
                .Select(n => n.Value)     // Convert back to int
                .ToArray();

                Channel<ItemToScan> channel = Channel.CreateUnbounded<ItemToScan>();
                MissionSupplier mc = new MissionSupplier(startIP, endIP, ports, channel);
                mc.Run();

            }
            
        }
    }
}