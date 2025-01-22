using System;
using System.Threading.Tasks;
using System.IO;
using SlumberProxy.Configuration;
using Newtonsoft.Json;
using System.Collections.Generic;
using SlumberProxy.Applications;
using Microsoft.Extensions.Logging;

namespace SlumberProxy;

class Program
{
    private static readonly TaskCompletionSource _shutdownEvent = new ();

    private static readonly List<ApplicationBase> applications = [];

    public static IReadOnlyList<ApplicationBase> Applications => applications;

    private static ILogger<Program> logger;

    static async Task Main(string[] args)
    {
        try
        {
            Globals.Setup(new(args));

            logger = Globals.LoggerFactory.CreateLogger<Program>();

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

            foreach (var applicationConfiguration in Globals.Configuration.Applications)
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
                await application.Start();

            logger.LogInformation("Slumber Proxy started, listening...");

            await _shutdownEvent.Task;

            logger.LogInformation("Shutting down...");

            foreach (var application in applications)
                application.Dispose();

            logger.LogInformation("Finished.");
        }
        catch (Exception ex)
        {
            logger?.LogCritical(ex, "Unhandled exception occurred in Main.");
            throw;
        }
        finally
        {
            Globals.Dispose();
        }
    }
}
