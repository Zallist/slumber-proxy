using System;
using System.Threading.Tasks;
using System.IO;
using ContainerSuspender.Configuration;
using Newtonsoft.Json;
using System.Collections.Generic;
using ContainerSuspender.Applications;
using Microsoft.Extensions.Logging;

namespace ContainerSuspender;

class Program
{
    private static readonly TaskCompletionSource _shutdownEvent = new ();

    private static string configPath = "config.json";
    private static SuspenderConfiguration configuration;
    private static readonly List<ApplicationBase> applications = [];

    public static SuspenderConfiguration Configuration => configuration;
    public static IReadOnlyList<ApplicationBase> Applications => applications;
    public static ILoggerFactory LoggerFactory { get; private set; }

    private static ILogger<Program> logger;

    static async Task Main(string[] args)
    {
        var argsList = new List<string>(args);

        if (argsList.Contains("-h") || argsList.Contains("--help"))
        {
            Console.WriteLine("Usage: " + System.AppDomain.CurrentDomain.FriendlyName + " [config-file] [-h | --help] [-v | --verbose]");
            return;
        }

        var logLevel = LogLevel.Information;

        if (argsList.Contains("-v") || argsList.Contains("--verbose"))
        {
            logLevel = LogLevel.Trace;
            argsList.Remove("-v");
            argsList.Remove("--verbose");
        }

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder
                .ClearProviders()
                .AddConsole()
                .SetMinimumLevel(logLevel);
        });

        logger = LoggerFactory.CreateLogger<Program>();

        AppDomain.CurrentDomain.ProcessExit += static (s, e) => _shutdownEvent.TrySetResult();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            logger.Log(
                e.IsTerminating ? LogLevel.Critical : LogLevel.Error, 
                e.ExceptionObject as Exception, 
                "Unhandled exception.");
        };

        Console.CancelKeyPress += static (s, e) =>
        {
            e.Cancel = true;
            _shutdownEvent.TrySetResult();
        };

        if (argsList.Count > 0)
            configPath = string.Join(" ", argsList);

        logger.LogInformation("Using config file '{configPath}'.", configPath);

        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file '{configPath}' not found.");

        ParseConfigFile();

        foreach (var applicationConfiguration in configuration.Applications)
        {
            ApplicationBase application = applicationConfiguration.ApplicationType switch
            {
                ApplicationType.Tcp => new TcpApplication(applicationConfiguration),
                ApplicationType.Udp => new UdpApplication(applicationConfiguration),
                _ => throw new NotImplementedException($"Application type '{applicationConfiguration.ApplicationType}' not implemented."),
            };

            applications.Add(application);
        }

        foreach (var application in applications)
            application.Start();

        //_ = Task.Factory.StartNew(() => HandlePing(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        logger.LogInformation("Suspender started, listening...");

        await _shutdownEvent.Task;

        logger.LogInformation("Shutting down...");

        foreach (var application in applications)
            application.Dispose();

        logger.LogInformation("Finished.");
    }

    private static void ParseConfigFile()
    {
        var configuration = JsonConvert.DeserializeObject<SuspenderConfiguration>(File.ReadAllText(configPath), new JsonSerializerSettings());

        Program.configuration = configuration ?? throw new Exception($"Failed to parse config file '{configPath}'.");
    }

    /*
    private static async ValueTask HandlePing()
    {
        try
        {
            // Create a raw socket to handle ICMP (Internet Control Message Protocol)
            using var icmpSocket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Raw, 
                System.Net.Sockets.ProtocolType.Icmp);

            icmpSocket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0)); // Bind to all network interfaces

            var buffer = new Memory<byte>(new byte[1024]);
            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);

            logger.LogInformation("Listening for ICMP requests...");

            while (true)
            {
                System.Net.EndPoint remoteEndPoint = null;

                try
                {
                    // Receive ICMP packets
                    var receiveFromResult = await icmpSocket.ReceiveFromAsync(buffer, endpoint);
                    var receivedBytes = receiveFromResult.ReceivedBytes;

                    remoteEndPoint = receiveFromResult.RemoteEndPoint;

                    logger.LogTrace("Received {receivedBytes} bytes from {remoteEndPoint}", receivedBytes, remoteEndPoint);

                    // Check if it's an ICMP Echo Request (Type 8, Code 0)
                    if (buffer.Span[20] == 8 && buffer.Span[21] == 0)
                    {
                        logger.LogTrace("ICMP Echo Request received. Sending response...");

                        // Create ICMP Echo Reply (Type 0, Code 0)
                        buffer.Span[20] = 0; // Set ICMP Type to 0 (Echo Reply)
                        ushort checksum = CalculateChecksum(buffer.Span, receivedBytes);
                        buffer.Span[22] = (byte)(checksum & 0xFF);       // Checksum (low byte)
                        buffer.Span[23] = (byte)((checksum >> 8) & 0xFF); // Checksum (high byte)

                        // Send the response back to the sender
                        await icmpSocket.SendToAsync(buffer.Slice(0, receivedBytes), System.Net.Sockets.SocketFlags.None, remoteEndPoint);
                        logger.LogTrace("Sent {receivedBytes} bytes to {remoteEndPoint}", receivedBytes, remoteEndPoint);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error handling ICMP request from {remoteEndPoint}.", remoteEndPoint);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling ANY ICMP request.");
        }

        static ushort CalculateChecksum(Span<byte> data, int length)
        {
            ulong sum = 0;

            for (int i = 20; i < length; i += 2)
                sum += (ushort)((data[i] << 8) + (i + 1 < length ? data[i + 1] : 0));

            while ((sum >> 16) != 0)
                sum = (sum & 0xFFFF) + (sum >> 16);

            return (ushort)~sum;
        }
    }
    */
}
