using System.Text.Json;

namespace SeedSearch;

internal static class SearchDiagnostics
{
    static readonly JsonSerializerOptions JsonOptions = new(SearchConfig.JsonOptions) { WriteIndented = false };
    public static string PathFor(string output) => Path.GetFullPath(output + ".errors.jsonl");

    public static string RecordFailure(string output, SearchConfig config, Exception error,
        SearchProgress progress, string checkpointPath, string eventName = "Failure", string detail = "")
    {
        string path = PathFor(output);
        var record = new
        {
            utc = DateTimeOffset.UtcNow,
            eventName,
            pid = Environment.ProcessId,
            output = Path.GetFullPath(output),
            checkpoint = checkpointPath,
            config = config,
            progress,
            error = ExceptionData(error),
            detail
        };
        try
        {
            DurableFile.AppendDurable(path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        }
        catch { /* diagnostics must never replace the original failure */ }
        return path;
    }

    public static string RecordEvent(string output, SearchConfig config, string eventName, string detail,
        SearchProgress progress, string checkpointPath)
        => RecordFailure(output, config, new InvalidOperationException(detail), progress, checkpointPath, eventName, detail);

    static object ExceptionData(Exception error) => new
    {
        type = error.GetType().FullName,
        message = error.Message,
        stack = error.StackTrace,
        inner = error.InnerException == null ? null : ExceptionData(error.InnerException)
    };
}
