using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SlumberProxy.Configuration;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace SlumberProxy.Applications;

public abstract class ApplicationBase : IDisposable
{
    protected Stopwatch activityCheckStopwatch = new();
    protected readonly DockerClient dockerClient;
    protected readonly ApplicationConfiguration configuration;
    protected readonly ILogger<ApplicationBase> logger;

    protected bool containerIsInactiveState = false;

    private readonly CancellationTokenSource cancellationTokenSource = new();

    public ApplicationBase(ApplicationConfiguration configuration)
    {
        logger = Globals.LoggerFactory.CreateLogger<ApplicationBase>();
        activityCheckStopwatch.Start();

        this.configuration = configuration;

        dockerClient = Globals.DockerManager.GetClient(configuration.DockerSocketUri);

        CheckIfContainerIsInactiveState();
    }

    public async ValueTask Start()
    {
        await StartApplication(cancellationTokenSource.Token);

        Globals.DockerManager.MonitorForAnyEvents(dockerClient, OnAnyDockerEvent);

        _ = Task.Factory.StartNew(() => InactivityCheck(cancellationTokenSource.Token), cancellationTokenSource.Token, TaskCreationOptions.PreferFairness, TaskScheduler.Default);
    }

    protected abstract ValueTask StartApplication(CancellationToken cancellationToken);

    private async void OnAnyDockerEvent(Message message)
    {
        if (!message.Type.Equals("container", StringComparison.OrdinalIgnoreCase)) return;

        var isThisContainer = false;

        await foreach (var id in GetContainerIds())
        {
            if (id.Equals(message.ID, StringComparison.OrdinalIgnoreCase))
            {
                isThisContainer = true;
                break;
            }
        }

        if (!isThisContainer) return;

        if (!containerIsInactiveState)
        {
            switch (message.Status)
            {
                case var status when
                        status.Equals("die", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("kill", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("pause", StringComparison.OrdinalIgnoreCase):

                    logger.LogInformation("Container '{configuration.DockerContainerName}' has been marked as '{status}'; marking as inactive...",
                        configuration.DockerContainerName,
                        message.Status);
                    containerIsInactiveState = true;
                    break;
                case var status when status.Equals("health_status", StringComparison.OrdinalIgnoreCase) &&
                        configuration.DockerContainerHealthCheck:
                    var inspect = await dockerClient.Containers.InspectContainerAsync(message.ID);

                    if (inspect == null || inspect.State.Health.Status != "healthy")
                    {
                        logger.LogInformation("Container '{configuration.DockerContainerName}' has failed health check; marking as inactive...", configuration.DockerContainerName);
                        containerIsInactiveState = true;
                    }
                    break;
            }
        }
        else if (EnsureContainerIsRunningTaskCompletionSource == null) // Check for weird states when we're NOT in charge of them
        {
            switch (message.Status)
            {
                case var status when
                        status.Equals("unpause", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("start", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("restart", StringComparison.OrdinalIgnoreCase):

                    logger.LogInformation("Container '{configuration.DockerContainerName}' has been marked as '{status}'; weird state, marking as inactive to double-check...",
                        configuration.DockerContainerName,
                        message.Status);
                    containerIsInactiveState = true;
                    break;
            }
        }
    }

    private async ValueTask InactivityCheck(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DoActivityCheck(cancellationToken);
            await Task.Delay(configuration.InactivityCheckInterval, cancellationToken);
        }
    }

    public void ActivityDetected() => activityCheckStopwatch.Restart();

    public async ValueTask<bool> DoActivityCheck(CancellationToken cancellationToken = default)
    {
        if (activityCheckStopwatch.Elapsed > configuration.InactiveContainerTime)
        {
            if (!containerIsInactiveState)
            {
                logger.LogInformation("Container '{configuration.DockerContainerName}' is inactive; suspending...", configuration.DockerContainerName);

                containerIsInactiveState = true;

                await foreach (var id in GetContainerIds(configuration.DockerContainerName, cancellationToken))
                    await ApplyToContainer(id);
            }
            else
            {
                // Container should already be inactive, so this is a sanity check to make sure it IS
                // Since it's possible that something ELSE restarted it - user command, autorestart on update, etc
                // And maybe it somehow bypassed the event system we have hooked up
                logger.LogDebug("Container '{configuration.DockerContainerName}' is inactive; checking it still is...", configuration.DockerContainerName);

                await foreach (var id in GetContainerIds(configuration.DockerContainerName, cancellationToken))
                    await ApplyToContainer(id);
            }

            activityCheckStopwatch.Restart(); // Restart the activity checker so we do intermittent checks regardless of state
        }

        return containerIsInactiveState;

        async ValueTask ApplyToContainer(string containerId)
        {
            switch (configuration.InactiveContainerAction)
            {
                case InactiveContainerAction.Pause:
                    await dockerClient.Containers.PauseContainerAsync(containerId, cancellationToken);
                    break;
                case InactiveContainerAction.Stop:
                    await dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters(), cancellationToken);
                    break;
                default:
                    throw new NotImplementedException($"{configuration.InactiveContainerAction} is not implemented.");
            }
        }
    }

    private bool CheckIfContainerIsInactiveState(CancellationToken cancellationToken = default)
    {
        var containerId = GetContainerId(cancellationToken).GetAwaiter().GetResult();

        var response = dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken).GetAwaiter().GetResult()
            ?? throw new Exception($"Container '{configuration.DockerContainerName}' not found.");

        return containerIsInactiveState = !response.State.Running || response.State.Paused;
    }

    private TaskCompletionSource<bool> EnsureContainerIsRunningTaskCompletionSource = null!;

    public async ValueTask<bool> EnsureContainerIsRunning(CancellationToken cancellationToken = default)
    {
        if (containerIsInactiveState)
        {
            bool isMaster = false;

            try
            {
                lock (this)
                {
                    if (EnsureContainerIsRunningTaskCompletionSource == null)
                    {
                        EnsureContainerIsRunningTaskCompletionSource = new();
                        isMaster = true;
                    }
                    else
                    {
                        isMaster = false;
                    }
                }

                if (EnsureContainerIsRunningTaskCompletionSource != null && !isMaster)
                    return await EnsureContainerIsRunningTaskCompletionSource.Task.ConfigureAwait(false);

                logger.LogInformation("Container '{configuration.DockerContainerName}' is inactive; starting...", configuration.DockerContainerName);

                bool didAnything = false;

                await foreach (var id in GetContainerIds(configuration.DockerContainerName, cancellationToken))
                {
                    var inspect = await dockerClient.Containers.InspectContainerAsync(id, cancellationToken);

                    if (inspect == null)
                    {
                        // TODO : Create the container
                        logger.LogError("Container '{configuration.DockerContainerName}', id: {id} not found. TODO : Create the container; Skipping for now.", configuration.DockerContainerName, id);
                        return false;
                    }

                    if (inspect.State.Paused)
                    {
                        await dockerClient.Containers.UnpauseContainerAsync(id, cancellationToken);
                        didAnything = true;
                    }
                    else if (!inspect.State.Running)
                    {
                        var started = await dockerClient.Containers.StartContainerAsync(id, new ContainerStartParameters(), cancellationToken);
                        didAnything = true;

                        if (!started)
                        {
                            // TODO : Create the container
                            logger.LogError("Failed to start container '{configuration.DockerContainerName}', id: {id}.", configuration.DockerContainerName, id);
                            return false;
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested) return false;

                if (didAnything)
                {
                    await Task.Delay(configuration.DockerContainerStartupTime, cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return false;
                }

                if (configuration.DockerContainerHealthCheck)
                {
                    var maxHealthCheckAttempts = (int)Math.Ceiling(TimeSpan.FromMinutes(5) / configuration.DockerContainerHealthCheckInterval);
                    var containerId = await GetContainerId(cancellationToken);
                    bool healthCheckPassed = false;

                    for (var healthCheckAttempt = 0; healthCheckAttempt < maxHealthCheckAttempts && !cancellationToken.IsCancellationRequested && !healthCheckPassed; healthCheckAttempt++)
                    {
                        logger.LogDebug("Checking if container '{configuration.DockerContainerName}' is healthy...", configuration.DockerContainerName);

                        var inspect = await dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);

                        if (inspect == null)
                        {
                            logger.LogWarning("Container '{configuration.DockerContainerName}' not found.", configuration.DockerContainerName);
                            healthCheckPassed = false;
                        }
                        else if (!inspect.State.Running)
                        {
                            logger.LogWarning("Container '{configuration.DockerContainerName}' is not running.", configuration.DockerContainerName);
                            healthCheckPassed = false;
                        }
                        else if (
                            !string.IsNullOrEmpty(inspect.State.Health.Status) &&
                            !inspect.State.Health.Status.Equals("healthy", StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogDebug("Container '{configuration.DockerContainerName}' is not healthy.", configuration.DockerContainerName);
                            healthCheckPassed = false;
                        }
                        else
                        {
                            healthCheckPassed = true;
                        }

                        if (!healthCheckPassed)
                            await Task.Delay(configuration.DockerContainerHealthCheckInterval, cancellationToken);
                    }

                    if (!healthCheckPassed)
                    {
                        logger.LogError("Container '{configuration.DockerContainerName}' is not healthy after {maxHealthCheckAttempts} attempts.", configuration.DockerContainerName, maxHealthCheckAttempts);
                        return false;
                    }
                }

                containerIsInactiveState = false;
                return containerIsInactiveState;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start container '{configuration.DockerContainerName}'.", configuration.DockerContainerName);
                return false;
            }
            finally
            {
                if (isMaster)
                {
                    EnsureContainerIsRunningTaskCompletionSource?.TrySetResult(!containerIsInactiveState);
                    EnsureContainerIsRunningTaskCompletionSource = null;
                }
            }
        }
        else
        {
            return !containerIsInactiveState;
        }
    }

    protected async Task<string> GetContainerId(CancellationToken cancellationToken = default)
    {
        await foreach (var id in GetContainerIds(configuration.DockerContainerName, cancellationToken))
            return id;

        return null;
    }

    protected IAsyncEnumerable<string> GetContainerIds(CancellationToken cancellationToken = default) =>
        GetContainerIds(configuration.DockerContainerName, cancellationToken);

    protected async IAsyncEnumerable<string> GetContainerIds(string dockerContainerIdOrName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var allContainers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters() { All = true }, cancellationToken);

        var baseContainer = allContainers.FirstOrDefault(c => c.Names.Contains($"/{dockerContainerIdOrName}"));

        if (string.IsNullOrEmpty(baseContainer?.ID)) // Double check in case somehow ID is empty
        {
            logger.LogError("Container '{dockerContainerIdOrName}' not found.", dockerContainerIdOrName);
            yield break;
        }

        // We always want to yield the base container
        yield return baseContainer.ID;
        logger.LogDebug("Container '{dockerContainerIdOrName}' found with ID '{baseContainer.ID}'.", dockerContainerIdOrName, baseContainer.ID);

        if (configuration.ApplyToDockerComposeGroup)
        {
            // If we care about the docker compose group, then we want to yield all containers in the group
            var composeProject = baseContainer.Labels["com.docker.compose.project"];

            if (string.IsNullOrEmpty(composeProject))
            {
                logger.LogInformation("Container '{dockerContainerIdOrName}' is not part of a docker compose group.", dockerContainerIdOrName);
            }
            else
            {
                foreach (var container in allContainers)
                {
                    if (container.ID == baseContainer.ID) continue; // Skip the base container

                    if (container.Labels["com.docker.compose.project"] == composeProject)
                    {
                        yield return container.ID;
                        logger.LogDebug("Compose-Related container for '{dockerContainerIdOrName}' found with ID '{container.ID}'.", dockerContainerIdOrName, container.ID);
                    }
                }
            }
        }
    }

    public virtual void Dispose()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();

        dockerClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    public bool CanIgnoreException(Exception ex)
    {
        logger.LogTrace(ex, "Checking exception");

        if (ex is OperationCanceledException) return true;
        if (ex is SocketException socketException && socketException.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Success or SocketError.OperationAborted) return true;

        if (ex.InnerException != null)
            return CanIgnoreException(ex.InnerException);

        return false;
    }
}
