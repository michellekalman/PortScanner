using System.Net.Sockets;
using System.Threading.Channels;
using System.Net;
using System.Text.Json;

namespace port_scanner
{
    
    public class ItemToScan(IPAddress ipAddress, int portNumber)
    {
        private readonly IPAddress ip = ipAddress;
        private readonly int port = portNumber;

        public IPAddress IP
        {
            get => ip;
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

        public static IEnumerable<IPAddress> GetIPRangeFromCIDR(IPAddress startIP, int subnet)
        {
            

            uint baseAddress = BitConverter.ToUInt32(startIP.GetAddressBytes().Reverse().ToArray(), 0);
            uint mask = uint.MaxValue << (32 - subnet);
            uint start = baseAddress & mask;
            uint end = start | ~mask;

            for (uint current = start + 1; current < end; current++) // Exclude network and broadcast
            {
                yield return new IPAddress(BitConverter.GetBytes(current).Reverse().ToArray());
            }
        }
    }
    public class MissionSupplier
    {
        
        private readonly ChannelWriter<ItemToScan> _writer;
        private readonly IPAddress _startIP;
        private readonly IPAddress? _endIP;
        private readonly int[] _ports;
        private readonly int _subnet;

        public MissionSupplier(IPAddress startIPAddr, int[] portArray, Channel<ItemToScan> unboundChannel, int subnet, IPAddress? endIPAddr = null)
        {
            _writer = unboundChannel.Writer;
            _startIP = startIPAddr;
            _endIP = endIPAddr;
            _ports = portArray;
            _subnet = subnet;
        }
        public async Task RunAsync(CancellationToken cancellationToken)
        {  
            List<IPAddress> ips;
            if (_endIP != null)
            {
                ips = [.. IPRangeGenerator.GetIPRange(_startIP, _endIP)];

            }
            else if(_subnet != -1){
                ips = [.. IPRangeGenerator.GetIPRangeFromCIDR(_startIP, _subnet)];
            }
            else{
                throw new ArgumentException("Invalid arguments.");
            }
            // Generate all IP and port combinations
            var ipPortPairs = ips.SelectMany(
                ip => _ports,
                (ip, port) => (IP: ip, Port: port)
            ).ToList();
            
            foreach (var (IP, Port) in ipPortPairs)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await _writer.WriteAsync(new ItemToScan(IP, Port), cancellationToken); 
                Console.WriteLine($"Message '{IP} {Port}' written to the channel.");
            } 

            _writer.Complete(); 
        }
            
    }

    public class Scanner(int numberOfTasks, Channel<ItemToScan> unboundChannel, string outputFileName) : IDisposable
    {
        private readonly ChannelReader<ItemToScan> _reader = unboundChannel.Reader;
        private readonly int _taskAmount = numberOfTasks;
        private readonly StreamWriter _outputFileWriter = new StreamWriter(outputFileName, append: true);
        private readonly object _fileLock = new object();

        public async Task StartScanAsync(CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            for (int i = 0; i < _taskAmount; i++)
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
            
            
                while (await _reader.WaitToReadAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;


                    if (_reader.TryRead(out ItemToScan task))
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

                        lock (_fileLock)
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

            Console.WriteLine("Enter ip range(cidr notation supported) and ports");
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
                IPAddress startIP;
                IPAddress? endIP = null;
                int subnet = -1;
                string[] words = input.Split(' ');

                if(input.Contains("/"))
                {
                    if (words.Length != 2){
                        Console.WriteLine("Invalid input!");
                        return;
                    }

                    var parts = words[0].Split('/');
                    if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out startIP) || !int.TryParse(parts[1], out subnet))
                    {
                        throw new ArgumentException("Invalid CIDR notation.");
                    }
                }

                else
                {
                    if (words.Length != 3)
                    {
                    Console.WriteLine("Invalid input!");
                    return;
                    }

                    // Parse the start and end IP strings to IPAddress objects
                    startIP = IPAddress.Parse(words[0]);
                    endIP = IPAddress.Parse(words[1]);

                    
                }
                string[] strPorts = words.Last().Split(',');
                int[] ports = strPorts
                .Select(s => int.TryParse(s, out int result) ? result : (int?)null)  // Use nullable int to handle failures
                .Where(n => n.HasValue)   // Filter out nulls (failed parses)
                .Select(n => n.Value)     // Convert back to int
                .ToArray();

                Channel<ItemToScan> channel = Channel.CreateUnbounded<ItemToScan>();
                MissionSupplier missionSupplier = new MissionSupplier(startIP, ports, channel, subnet, endIP);
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
