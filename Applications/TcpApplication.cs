using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ContainerSuspender.Configuration;
using Microsoft.Extensions.Logging;

namespace ContainerSuspender.Applications
{
    public class TcpApplication(ApplicationConfiguration configuration) : ApplicationBase(configuration)
    {
        private TcpListener tcpListener;

        protected override ValueTask StartApplication(CancellationToken cancellationToken)
        {
            tcpListener = new TcpListener(IPAddress.Parse("0.0.0.0"), configuration.ListenPort)
            {
                ExclusiveAddressUse = true
            };

#if WINDOWS
            tcpListener.AllowNatTraversal(true);
#endif

            tcpListener.Start();

            _ = Task.Factory.StartNew(() => MonitorAndForwardTraffic(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            return ValueTask.CompletedTask;
        }

        private async ValueTask MonitorAndForwardTraffic(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await tcpListener.AcceptTcpClientAsync(token);

                    if (tcpClient == null || token.IsCancellationRequested)
                    {
                        tcpClient?.Dispose();
                        continue;
                    }

                    _ = Task.Run(() => HandleTcpConnection(tcpClient, token), token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error accepting TCP connection, configuration: {configuration}", configuration);
                }
            }
        }

        private async ValueTask HandleTcpConnection(TcpClient tcpClient, CancellationToken cancellationToken)
        {
            ActivityDetected();

            await EnsureContainerIsRunning(cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            using var tcpForwarder = new TcpClient()
            {
                NoDelay = true,

                ReceiveBufferSize = 1024 * 1024 * 1024, // 1GB
                ReceiveTimeout = (int)configuration.InactiveContainerTime.TotalMilliseconds,

                SendBufferSize = 1024 * 1024 * 1024, // 1GB
                SendTimeout = (int)configuration.InactiveContainerTime.TotalMilliseconds,
            };

            await tcpForwarder.ConnectAsync(configuration.TargetAddress, configuration.TargetPort, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;
            
            var clientStream = tcpClient.GetStream();
            var forwarderStream = tcpForwarder.GetStream();

            // Bi-directional stream copying
            var clientToForwarderTask = CopyStreamAsync(clientStream, forwarderStream, cancellationToken);
            var forwarderToClientTask = CopyStreamAsync(forwarderStream, clientStream, cancellationToken);

            // Wait for either task to complete
            await Task.WhenAny(clientToForwarderTask, forwarderToClientTask);

            // Close connections gracefully
            tcpClient.Close();
            tcpForwarder.Close();

            // Dispose them
            tcpClient.Dispose();
            tcpForwarder.Dispose();

            // AND AT THE END
            ActivityDetected();
        }

        private async Task CopyStreamAsync(NetworkStream input, NetworkStream output, CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new Memory<byte>(new byte[8192]);

                while (true)
                {
                    var bytesRead = await input.ReadAsync(buffer, cancellationToken);

                    if (bytesRead == 0)
                        break;

                    await output.WriteAsync(buffer.Slice(0, bytesRead), cancellationToken);
                    await output.FlushAsync(cancellationToken); // Ensure the data is sent immediately

                    ActivityDetected();
                }
            }
            catch (Exception ex)
            {
                if (CanIgnoreException(ex)) return;

                logger.LogError(ex, "Error during stream copy");
            }
        }

        public override void Dispose()
        {
            base.Dispose();

            tcpListener?.Dispose();
            tcpListener = null;

            GC.SuppressFinalize(this);
        }
    }
}
