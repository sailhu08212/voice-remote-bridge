using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed class InputGuardClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private StreamReader? reader;
    private StreamWriter? writer;
    private bool disposed;

    public InputGuardClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public bool IsConnected => pipe.IsConnected;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pipe.IsConnected)
        {
            return;
        }

        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token,
            cancellationToken);
        await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
        reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    public Task<GuardResponse> PingAsync(CancellationToken cancellationToken = default) => SendAsync(
        new GuardRequest { Operation = GuardOperation.Ping },
        cancellationToken);

    public Task<GuardResponse> HoldAsync(
        long epoch,
        IReadOnlyList<ushort> keys,
        KeyInjectionMode mode,
        CancellationToken cancellationToken = default) => SendAsync(
            new GuardRequest
            {
                Operation = GuardOperation.Hold,
                Epoch = epoch,
                Keys = keys,
                InjectionMode = mode.ToString()
            },
            cancellationToken);

    public Task<GuardResponse> RenewAsync(long epoch, CancellationToken cancellationToken = default) => SendAsync(
        new GuardRequest { Operation = GuardOperation.Renew, Epoch = epoch },
        cancellationToken);

    public Task<GuardResponse> ReleaseAsync(long epoch, CancellationToken cancellationToken = default) => SendAsync(
        new GuardRequest { Operation = GuardOperation.Release, Epoch = epoch },
        cancellationToken);

    public Task<GuardResponse> ShutdownAsync(long epoch, CancellationToken cancellationToken = default) => SendAsync(
        new GuardRequest { Operation = GuardOperation.Shutdown, Epoch = epoch },
        cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (writer is not null)
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The guard may have closed its end immediately after acknowledging Shutdown.
            }
        }

        if (reader is not null)
        {
            try
            {
                reader.Dispose();
            }
            catch (IOException)
            {
            }
        }

        try
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        requestLock.Dispose();
    }

    private async Task<GuardResponse> SendAsync(GuardRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!pipe.IsConnected || reader is null || writer is null)
        {
            throw new InvalidOperationException("InputGuard pipe is not connected.");
        }

        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string requestJson = JsonSerializer.Serialize(request, JsonOptions);
            await writer.WriteLineAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            string? responseJson = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            GuardResponse response = responseJson is null
                ? throw new EndOfStreamException("InputGuard closed the pipe before responding.")
                : JsonSerializer.Deserialize<GuardResponse>(responseJson, JsonOptions)
                  ?? throw new InvalidDataException("InputGuard returned an empty response.");
            if (response.RequestId != request.RequestId)
            {
                throw new InvalidDataException("InputGuard response id did not match the request.");
            }

            return response;
        }
        finally
        {
            requestLock.Release();
        }
    }
}
