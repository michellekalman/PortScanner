using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.VisualBasic;
using System.Linq;
using System.Net;
using port_scanner;
using System.Text.Json;

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
        
        private readonly ChannelWriter<ItemToScan> writer;
        private IPAddress startIP;
        private IPAddress endIP;
        private int[] ports;

        public MissionSupplier(IPAddress startIPAddr, IPAddress endIPAddr, int[] portArray, Channel<ItemToScan> unboundChannel)
        {
            writer = unboundChannel.Writer;
            startIP = startIPAddr;
            endIP = endIPAddr;
            ports = portArray;
        }
        public async Task RunAsync(CancellationToken cancellationToken)
        {  
            List<IPAddress> ips = [.. IPRangeGenerator.GetIPRange(startIP, endIP)];

            // Generate all IP and port combinations
            var ipPortPairs = ips.SelectMany(
                ip => ports,
                (ip, port) => (IP: ip, Port: port)
            ).ToList();

            foreach (var (IP, Port) in ipPortPairs)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await writer.WriteAsync(new ItemToScan(IP, Port), cancellationToken); 
                Console.WriteLine($"Message '{IP} {Port}' written to the channel.");
            } 

            writer.Complete(); 
        }
            
    }

    public class Scanner : IDisposable
    {
        private readonly ChannelReader<ItemToScan> reader;
        private readonly int taskAmount;
        private readonly StreamWriter _outputFileWriter;
        private readonly object fileLock = new object();
        

        public Scanner(int numberOfTasks, Channel<ItemToScan> unboundChannel, string outputFileName)
        {
            reader = unboundChannel.Reader;
            taskAmount = numberOfTasks;
            _outputFileWriter = new StreamWriter(outputFileName, append: true);
            
        }

        public async Task StartScanAsync(CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            for (int i = 0; i < taskAmount; i++)
            {
                tasks.Add(Task.Run(() => ScanPortsAsync(cancellationToken), cancellationToken));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Scanning tasks were canceled.");
            }
        }

        public async Task ScanPortsAsync(CancellationToken cancellationToken)
        {
            try
            {
            
            
                while (await reader.WaitToReadAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;


                    if (reader.TryRead(out ItemToScan task))
                    {
                        var ip = task.IP;
                        var port = task.Port;
                        bool IsPortOpen = await IsPortOpenAsync(ip, port, cancellationToken);                  
                        
                        var result = new
                        {
                            TargetIp = ip.ToString(),
                            Port = port,
                            IsOpen = IsPortOpen
                        };

                        lock (fileLock)
                        {
                            _outputFileWriter.WriteLine(JsonSerializer.Serialize(result));
                            _outputFileWriter.Flush(); // Ensure data is written immediately
                        }
                    }
                }
            }

            catch (OperationCanceledException)
            {
                // Expected exception on cancellation; silently exit the loop
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ScanPortsAsync: {ex.Message}");
            }
        }

        private async Task<bool> IsPortOpenAsync(IPAddress ip, int port, CancellationToken cancellationToken, int timeout = 1000)
        {
            using var client = new TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(ip.ToString(), port);
                return await Task.WhenAny(connectTask, Task.Delay(timeout, cancellationToken)) == connectTask;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _outputFileWriter.Dispose();
        }

    }

       
    
    public class Program
    {
        public static async Task Main(string[] args)
        {

            using var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            Console.WriteLine("Enter start ip address, end ip address and ports");
            string? input = Console.ReadLine();


            var waitForExitTask = Task.Run(() =>
            {
                while (true)
                {
                    string additionalInput = Console.ReadLine()?.Trim().ToLower();
                    if (additionalInput == "exit")
                    {
                        Console.WriteLine("Exiting program...");
                        cancellationTokenSource.Cancel(); // Signal cancellation to all tasks
                        break;
                    }
                }
            });

            if(!string.IsNullOrEmpty(input))
            {
                string[] words = input.Split(' ');
                if (words.Length != 3){
                    Console.WriteLine("Invalid input!");
                    return;
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
                MissionSupplier missionSupplier = new MissionSupplier(startIP, endIP, ports, channel);
                Scanner scanner = new(10, channel, "output_file.txt");
                Task supplierTask = missionSupplier.RunAsync(cancellationToken);
                Task scannerTask = scanner.StartScanAsync(cancellationToken);


                await Task.WhenAny(Task.WhenAll(supplierTask, scannerTask), waitForExitTask);

                // Wait for remaining tasks to finish if cancellation was requested
                await supplierTask;
                await scannerTask;
                }
            else
                {
                    Console.WriteLine("No input received.");
                }

            Console.WriteLine("Program has exited.");

        }
            
    }
}
