using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lab6;

internal static class SlaveClient
{
    private const string ComputeCommand = "COMPUTE_L2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<SlaveResponse> RequestL2Async(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        await client.ConnectAsync(host, port, cancellationToken);

        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        await writer.WriteLineAsync(ComputeCommand.AsMemory(), cancellationToken);
        string? json = await reader.ReadLineAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new IOException("slave-вузол не повернув відповідь");
        }

        return JsonSerializer.Deserialize<SlaveResponse>(json, JsonOptions)
            ?? throw new IOException("отримано некоректну JSON-відповідь від slave-вузла");
    }
}

internal sealed record SlaveResponse(
    bool Success,
    string Message,
    string MachineName,
    string OperatingSystem,
    DateTimeOffset ComputedAtUtc,
    double? Eigenvalue,
    double? ShiftedEigenvalue,
    double[]? Eigenvector,
    double? ResidualNorm,
    int? Iterations,
    double? LowerGershgorinBound,
    double? Shift,
    double? ElapsedMilliseconds);
