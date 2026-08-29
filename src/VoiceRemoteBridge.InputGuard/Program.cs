using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VoiceRemoteBridge.Core;
using VoiceRemoteBridge.Windows;

namespace VoiceRemoteBridge.InputGuard;

internal static class Program
{
    private static Task<int> Main(string[] args) => InputGuardHost.RunAsync(args);
}

public static class InputGuardHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            GuardOptions options = GuardOptions.Parse(args);
            using Mutex singleInstance = new(initiallyOwned: true, $"Local\\VoiceRemoteBridge.InputGuard.{options.SessionToken}", out bool createdNew);
            if (!createdNew)
            {
                return 2;
            }

            using GuardServer server = new(options);
            await server.RunAsync().ConfigureAwait(false);
            GC.KeepAlive(singleInstance);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"InputGuard failed: {exception.Message}");
            return 1;
        }
    }
}

internal sealed record GuardOptions(
    string SessionToken,
    string PipeName,
    int ParentProcessId,
    TimeSpan LeaseDuration)
{
    internal static GuardOptions Parse(string[] args)
    {
        string session = GetRequired(args, "--session");
        if (!int.TryParse(GetRequired(args, "--parent"), out int parentProcessId) || parentProcessId <= 0)
        {
            throw new ArgumentException("--parent must be a positive process id.");
        }

        int leaseMilliseconds = GuardProtocol.DefaultLeaseMilliseconds;
        string? rawLease = GetOption(args, "--lease-ms");
        if (rawLease is not null && (!int.TryParse(rawLease, out leaseMilliseconds) || leaseMilliseconds is < 500 or > 30_000))
        {
            throw new ArgumentException("--lease-ms must be between 500 and 30000.");
        }

        return new GuardOptions(
            session,
            GuardProtocol.BuildPipeName(session),
            parentProcessId,
            TimeSpan.FromMilliseconds(leaseMilliseconds));
    }

    private static string GetRequired(string[] args, string name) =>
        GetOption(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

    private static string? GetOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

internal sealed class GuardServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly GuardOptions options;
    private readonly Win32KeyInjector injector = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateLock = new();
    private readonly Process parentProcess;
    private long activeEpoch = -1;
    private DateTimeOffset leaseDeadline = DateTimeOffset.MinValue;
    private KeyInjectionMode activeMode = KeyInjectionMode.VirtualKey;
    private bool disposed;

    internal GuardServer(GuardOptions options)
    {
        this.options = options;
        parentProcess = Process.GetProcessById(options.ParentProcessId);
        parentProcess.EnableRaisingEvents = true;
        parentProcess.Exited += ParentExited;
    }

    internal async Task RunAsync()
    {
        Task leaseMonitor = MonitorLeaseAsync(lifetime.Token);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await using NamedPipeServerStream pipe = new(
                    options.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    await ProcessConnectionAsync(pipe, lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // A disconnected client is recovered by accepting a new connection. The lease
                    // monitor remains responsible for releasing a held chord.
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            await leaseMonitor.ConfigureAwait(false);
            ReleaseWithRetries();
        }
    }

    private async Task ProcessConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true
        };

        while (!cancellationToken.IsCancellationRequested && stream.CanRead)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            GuardResponse response;
            try
            {
                GuardRequest request = JsonSerializer.Deserialize<GuardRequest>(line, JsonOptions)
                    ?? throw new InvalidDataException("Guard request was empty.");
                response = HandleRequest(request);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                response = new GuardResponse
                {
                    RequestId = Guid.Empty,
                    Succeeded = false,
                    ActiveEpoch = activeEpoch,
                    KeysHeld = injector.HeldKeys.Count > 0,
                    Message = exception.Message
                };
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
        }
    }

    private GuardResponse HandleRequest(GuardRequest request)
    {
        if (request.ProtocolVersion != GuardProtocol.CurrentVersion)
        {
            return Failure(request, 0, $"Unsupported protocol version {request.ProtocolVersion}.");
        }

        lock (stateLock)
        {
            return request.Operation switch
            {
                GuardOperation.Ping => Success(request, "pong"),
                GuardOperation.Hold => Hold(request),
                GuardOperation.Renew => Renew(request),
                GuardOperation.Release => Release(request),
                GuardOperation.Shutdown => Shutdown(request),
                _ => Failure(request, 0, "Unsupported guard operation.")
            };
        }
    }

    private GuardResponse Hold(GuardRequest request)
    {
        if (request.Epoch < activeEpoch)
        {
            return Failure(request, 0, "Stale epoch was rejected.");
        }

        if (request.Keys.Count == 0)
        {
            return Failure(request, 0, "Hold requires at least one key.");
        }

        KeyInjectionMode mode = ParseMode(request.InjectionMode);
        if (injector.HeldKeys.Count > 0)
        {
            if (request.Epoch == activeEpoch && injector.HeldKeys.SequenceEqual(request.Keys))
            {
                leaseDeadline = DateTimeOffset.UtcNow + options.LeaseDuration;
                return Success(request, "Idempotent hold renewed the active lease.");
            }

            InjectionResult release = ReleaseWithRetries();
            if (!release.Succeeded)
            {
                return Failure(request, release.Win32Error, release.Message);
            }
        }

        InjectionResult result = injector.Hold(request.Keys, mode);
        if (!result.Succeeded)
        {
            return Failure(request, result.Win32Error, result.Message);
        }

        activeEpoch = request.Epoch;
        activeMode = mode;
        leaseDeadline = DateTimeOffset.UtcNow + options.LeaseDuration;
        return Success(request, "Chord held.");
    }

    private GuardResponse Renew(GuardRequest request)
    {
        if (request.Epoch != activeEpoch || injector.HeldKeys.Count == 0)
        {
            return Failure(request, 0, "No matching active hold to renew.");
        }

        leaseDeadline = DateTimeOffset.UtcNow + options.LeaseDuration;
        return Success(request, "Lease renewed.");
    }

    private GuardResponse Release(GuardRequest request)
    {
        if (request.Epoch < activeEpoch)
        {
            return Success(request, "Stale release ignored.");
        }

        if (request.Epoch > activeEpoch && injector.HeldKeys.Count > 0)
        {
            return Failure(request, 0, "Future epoch cannot release the active hold.");
        }

        InjectionResult result = ReleaseWithRetries();
        if (!result.Succeeded)
        {
            return Failure(request, result.Win32Error, result.Message);
        }

        activeEpoch = Math.Max(activeEpoch, request.Epoch);
        leaseDeadline = DateTimeOffset.MinValue;
        return Success(request, "Chord released.");
    }

    private GuardResponse Shutdown(GuardRequest request)
    {
        InjectionResult result = ReleaseWithRetries();
        if (!result.Succeeded)
        {
            return Failure(request, result.Win32Error, result.Message);
        }

        lifetime.Cancel();
        return Success(request, "Guard shutting down.");
    }

    private async Task MonitorLeaseAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (stateLock)
                {
                    if (injector.HeldKeys.Count > 0 &&
                        leaseDeadline != DateTimeOffset.MinValue &&
                        DateTimeOffset.UtcNow >= leaseDeadline)
                    {
                        ReleaseWithRetries();
                        leaseDeadline = DateTimeOffset.MinValue;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private InjectionResult ReleaseWithRetries()
    {
        InjectionResult result = injector.ReleaseAll(activeMode);
        for (int attempt = 1; attempt < 3 && !result.Succeeded; attempt++)
        {
            Thread.Sleep(10 * attempt);
            result = injector.ReleaseAll(activeMode);
        }

        return result;
    }

    private GuardResponse Success(GuardRequest request, string message) => new()
    {
        RequestId = request.RequestId,
        Succeeded = true,
        ActiveEpoch = activeEpoch,
        KeysHeld = injector.HeldKeys.Count > 0,
        Message = message
    };

    private GuardResponse Failure(GuardRequest request, int error, string message) => new()
    {
        RequestId = request.RequestId,
        Succeeded = false,
        ActiveEpoch = activeEpoch,
        KeysHeld = injector.HeldKeys.Count > 0,
        Message = message,
        Win32Error = error
    };

    private static KeyInjectionMode ParseMode(string mode) => mode switch
    {
        "VirtualKey" => KeyInjectionMode.VirtualKey,
        "ScanCode" => KeyInjectionMode.ScanCode,
        _ => throw new ArgumentException($"Unsupported injection mode: {mode}.")
    };

    private void ParentExited(object? sender, EventArgs eventArgs)
    {
        lock (stateLock)
        {
            ReleaseWithRetries();
        }

        lifetime.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        parentProcess.Exited -= ParentExited;
        parentProcess.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
        disposed = true;
    }
}
