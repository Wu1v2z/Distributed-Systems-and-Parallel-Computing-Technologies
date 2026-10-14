using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Lab5;

internal static class SlaveProtocol
{
    public const int DefaultPort = 5055;
    public const string ComputeCommand = "COMPUTE_L2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunServerAsync(
        IPAddress bindAddress,
        int port,
        bool oneRequestOnly,
        double tolerance,
        int maximumIterations,
        CancellationToken cancellationToken)
    {
        TcpListener listener = new(bindAddress, port);
        listener.Start();

        Console.WriteLine("ЛР5 — slave-вузол обчислення L2");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"Вузол: {Environment.MachineName}");
        Console.WriteLine($"ОС: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Прослуховування: {bindAddress}:{port}");
        Console.WriteLine($"Режим: {(oneRequestOnly ? "один запит" : "до натискання Ctrl+C")}");
        Console.WriteLine($"Команда протоколу: {ComputeCommand}");
        Console.WriteLine();

        try
        {
            do
            {
                Console.WriteLine("Очікування запиту від host-комп’ютера...");
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                await HandleClientAsync(
                    client,
                    tolerance,
                    maximumIterations,
                    cancellationToken);
            }
            while (!oneRequestOnly && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Slave-вузол зупинено.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task<SlaveResponse> RequestResultAsync(
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
            ?? throw new IOException("отримано некоректну JSON-відповідь");
    }

    private static async Task HandleClientAsync(
        TcpClient client,
        double tolerance,
        int maximumIterations,
        CancellationToken cancellationToken)
    {
        string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "невідомий вузол";
        Console.WriteLine($"Підключено host-вузол: {remoteEndpoint}");

        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        string? command = await reader.ReadLineAsync(cancellationToken);
        if (!string.Equals(command, ComputeCommand, StringComparison.Ordinal))
        {
            await SendAsync(
                writer,
                SlaveResponse.Failure($"невідома команда «{command ?? "порожньо"}»"),
                cancellationToken);
            return;
        }

        Stopwatch timer = Stopwatch.StartNew();
        EigenResult result = EigenvalueSolver.FindLargestAlgebraic(
            MatrixData.CreateBd1(),
            tolerance,
            maximumIterations);
        timer.Stop();

        SlaveResponse response = SlaveResponse.FromResult(result, timer.Elapsed.TotalMilliseconds);
        await SendAsync(writer, response, cancellationToken);

        Console.WriteLine($"Обчислення завершено на вузлі {response.MachineName}.");
        Console.WriteLine($"L2 = {result.Eigenvalue:F12}; ітерацій: {result.Iterations}; " +
                          $"нев’язка: {result.ResidualNorm:E6}");
        Console.WriteLine("Результат надіслано host-комп’ютеру.");
        Console.WriteLine();
    }

    private static async Task SendAsync(
        StreamWriter writer,
        SlaveResponse response,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(response, JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
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
    double? ElapsedMilliseconds)
{
    public static SlaveResponse FromResult(EigenResult result, double elapsedMilliseconds) => new(
        result.Converged,
        result.Converged ? "Обчислення L2 успішно завершено" : "Досягнуто ліміт ітерацій",
        Environment.MachineName,
        RuntimeInformation.OSDescription,
        DateTimeOffset.UtcNow,
        result.Eigenvalue,
        result.ShiftedEigenvalue,
        result.Eigenvector,
        result.ResidualNorm,
        result.Iterations,
        result.LowerGershgorinBound,
        result.Shift,
        elapsedMilliseconds);

    public static SlaveResponse Failure(string message) => new(
        false,
        message,
        Environment.MachineName,
        RuntimeInformation.OSDescription,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}
