using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ContainerSuspender.Configuration;
using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace ContainerSuspender.Applications
{
    public class UdpApplication(ApplicationConfiguration configuration) : ApplicationBase(configuration)
    {
        private class UdpForwarder(IPEndPoint endpoint, UdpClient client)
        {
            public readonly IPEndPoint Endpoint = endpoint;
            public readonly UdpClient Client = client;

            public DateTime LastActivity = DateTime.UtcNow;
        }

        private UdpClient udpListener;
        private readonly Dictionary<IPEndPoint, UdpForwarder> udpForwarders = new();

        protected override ValueTask StartApplication(CancellationToken cancellationToken)
        {
            udpListener = new UdpClient(configuration.ListenPort)
            {
                DontFragment = false,
                EnableBroadcast = true,
                Ttl = 255,
            };

#if WINDOWS
            udpListener.AllowNatTraversal(true);
#endif

            _ = Task.Factory.StartNew(() => MonitorAndForwardTraffic(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _ = Task.Factory.StartNew(() => UdpClientCleanup(cancellationToken), cancellationToken, TaskCreationOptions.PreferFairness, TaskScheduler.Default);

            return ValueTask.CompletedTask;
        }

        private async ValueTask UdpClientCleanup(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HashSet<UdpForwarder> remove = null;

                foreach (var forwarder in udpForwarders.Values)
                {
                    if (forwarder.LastActivity + configuration.InactiveContainerTime < DateTime.UtcNow)
                    {
                        forwarder.Client?.Dispose();

                        remove ??= new();
                        remove.Add(forwarder);
                    }
                }

                if (remove != null)
                    foreach (var forwarder in remove)
                        udpForwarders.Remove(forwarder.Endpoint);

                await Task.Delay(configuration.InactivityCheckInterval, cancellationToken);
            }
        }

        private async ValueTask MonitorAndForwardTraffic(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var udpClientReceiveResult = await udpListener.ReceiveAsync(token);

                    if (token.IsCancellationRequested)
                        continue;

                    if (logger.IsEnabled(LogLevel.Trace))
                    {
                        logger.LogTrace("Received UDP packet from {endpoint}, data: {data}", 
                            udpClientReceiveResult.RemoteEndPoint, 
                            System.Text.Encoding.UTF8.GetString(udpClientReceiveResult.Buffer));
                    }

                    var endpoint = udpClientReceiveResult.RemoteEndPoint;

                    if (!udpForwarders.TryGetValue(udpClientReceiveResult.RemoteEndPoint, out var forwarder))
                    {
                        logger.LogTrace("Creating new forwarder for {endpoint}", endpoint);

                        forwarder = new(udpClientReceiveResult.RemoteEndPoint, new UdpClient(configuration.TargetAddress, configuration.TargetPort) 
                        { 
                            EnableBroadcast = true,
                            DontFragment = false,
                            Ttl = 255
                        });

                        udpForwarders.Add(udpClientReceiveResult.RemoteEndPoint, forwarder);

                        _ = Task.Factory.StartNew(() => HandleUdpResponses(forwarder, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }
                    else
                    {
                        logger.LogTrace("Using existing forwarder for {endpoint}", endpoint);
                    }

                    _ = Task.Run(() => HandleUdpRequest(udpClientReceiveResult.Buffer, forwarder, token), token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error accepting UDP connection, configuration: {configuration}", configuration);
                }
            }
        }

        private async ValueTask HandleUdpRequest(byte[] udpBuffer, UdpForwarder forwarder, CancellationToken cancellationToken)
        {
            forwarder.LastActivity = DateTime.UtcNow;
            ActivityDetected();

            await EnsureContainerIsRunning(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await forwarder.Client.SendAsync(udpBuffer, udpBuffer.Length)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (CanIgnoreException(ex)) return;

                logger.LogError(ex, "Error during udp request");
                forwarder.Client?.Dispose();
                udpForwarders.Remove(forwarder.Endpoint);
            }
        }

        private async ValueTask HandleUdpResponses(UdpForwarder forwarder, CancellationToken cancellationToken)
        {
            forwarder.LastActivity = DateTime.UtcNow;
            ActivityDetected();

            await EnsureContainerIsRunning(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var udpForwarderResponse = await forwarder.Client.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                    continue;

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Received UDP response from {endpoint}, data: {data}",
                        forwarder.Endpoint,
                        System.Text.Encoding.UTF8.GetString(udpForwarderResponse.Buffer));
                }

                forwarder.LastActivity = DateTime.UtcNow;
                ActivityDetected();

                await udpListener.SendAsync(udpForwarderResponse.Buffer, udpForwarderResponse.Buffer.Length, forwarder.Endpoint)
                    .ConfigureAwait(false);
            }

            // AND AT THE END
            forwarder.LastActivity = DateTime.UtcNow;
            ActivityDetected();
        }

        public override void Dispose()
        {
            base.Dispose();

            udpListener?.Dispose();
            udpListener = null;

            foreach (var forwarder in udpForwarders.Values)
                forwarder.Client?.Dispose();

            udpForwarders.Clear();

            GC.SuppressFinalize(this);
        }
    }
}
