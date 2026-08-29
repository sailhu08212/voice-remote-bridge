using System.Text;
using System.Text.Json;
using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    bool LoadedExistingFile,
    IReadOnlyList<string> Errors);

public sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public JsonSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public static JsonSettingsStore CreateDefault()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceRemoteBridge");
        return new JsonSettingsStore(Path.Combine(root, "settings.json"));
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new SettingsLoadResult(new AppSettings(), false, []);
        }

        try
        {
            await using FileStream stream = new(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                useAsync: true);
            AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                return new SettingsLoadResult(new AppSettings(), true, ["Settings file was empty."]);
            }

            IReadOnlyList<string> validationErrors = settings.Validate();
            return validationErrors.Count == 0
                ? new SettingsLoadResult(settings, true, [])
                : new SettingsLoadResult(settings, true, validationErrors);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(
                new AppSettings(),
                true,
                [$"Unable to load settings: {exception.Message}"]);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IReadOnlyList<string> errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        string directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4_096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
