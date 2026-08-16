using System.Globalization;
using System.Text;

namespace VstOnlineStore.Observability;

/// <summary>
/// Schreibt genau ein JSON-Objekt pro Zeile in eine UTC-Tagesdatei des
/// jeweiligen Services und entfernt abgelaufene Tagesdateien.
/// </summary>
internal sealed class DailyJsonLogFileSink(
    StructuredLoggingOptions options) : IDisposable {

    private readonly Lock _sync = new();
    private StreamWriter? _writer;
    private DateOnly? _openDate;
    private bool _disposed;

    public void Write(DateTime utcTimestamp, string json) {
        var utcDate = DateOnly.FromDateTime(utcTimestamp);

        lock (_sync) {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_writer is null || _openDate != utcDate) {
                OpenFile(utcDate);
            }

            _writer!.WriteLine(json);
            _writer.Flush();
        }
    }

    public void Dispose() {
        lock (_sync) {
            if (_disposed) {
                return;
            }

            _writer?.Dispose();
            _writer = null;
            _disposed = true;
        }
    }

    private void OpenFile(DateOnly utcDate) {
        _writer?.Dispose();

        var serviceDirectory = Path.Combine(
            options.LogRootDirectory,
            SanitizePathSegment(options.ServiceName));
        Directory.CreateDirectory(serviceDirectory);

        DeleteExpiredFiles(serviceDirectory, utcDate);

        var path = Path.Combine(
            serviceDirectory,
            $"{SanitizePathSegment(options.ServiceName)}-{utcDate:yyyy-MM-dd}.jsonl");
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _openDate = utcDate;
    }

    private void DeleteExpiredFiles(string serviceDirectory, DateOnly currentUtcDate) {
        var firstRetainedDate = currentUtcDate.AddDays(-(options.RetentionDays - 1));
        var prefix = $"{SanitizePathSegment(options.ServiceName)}-";

        foreach (var filePath in Directory.EnumerateFiles(
            serviceDirectory,
            $"{SanitizePathSegment(options.ServiceName)}-????-??-??.jsonl")) {

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var dateText = fileName[prefix.Length..];
            if (DateOnly.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var fileDate)
                && fileDate < firstRetainedDate) {
                try {
                    File.Delete(filePath);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException) {
                    System.Diagnostics.Debug.WriteLine(
                        $"Expired structured log could not be deleted: {exception.Message}");
                }
            }
        }
    }

    private static string SanitizePathSegment(string value) {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
    }
}
