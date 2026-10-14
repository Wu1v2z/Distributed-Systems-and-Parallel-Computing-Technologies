using System.Diagnostics;
using System.Text;

namespace Lab1;

internal static class Program
{
    private const int ArrayLength = 9_000_000;
    private const int MaxValue = 100;

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuTimeBefore = process.TotalProcessorTime;
        Stopwatch programTimer = Stopwatch.StartNew();

        int[] numbers = CreateBoundedArray(ArrayLength);
        ArrayPart[] parts = SplitIntoThreeParts(numbers.Length);
        PartResult?[] results = new PartResult?[parts.Length];

        Stopwatch parallelTimer = Stopwatch.StartNew();
        CreateAndAwaitWorkersRecursively(numbers, parts, results, partIndex: 0);
        parallelTimer.Stop();

        long totalSum = results.Sum(result => result!.Sum);
        long expectedSum = CalculateExpectedSum(numbers.Length);

        programTimer.Stop();
        process.Refresh();
        TimeSpan processorTime = process.TotalProcessorTime - cpuTimeBefore;

        PrintReport(
            numbers,
            parts,
            results,
            totalSum,
            expectedSum,
            parallelTimer.Elapsed,
            programTimer.Elapsed,
            processorTime);
    }

    private static int[] CreateBoundedArray(int length)
    {
        int[] numbers = new int[length];

        for (int index = 0; index < numbers.Length; index++)
        {
            // Значення циклічно повторюються від 1 до 100.
            numbers[index] = index % MaxValue + 1;
        }

        return numbers;
    }

    private static ArrayPart[] SplitIntoThreeParts(int arrayLength)
    {
        // Довільний нерівномірний поділ: 20 %, 30 % та 50 % елементів.
        int firstPartLength = arrayLength * 20 / 100;
        int secondPartLength = arrayLength * 30 / 100;
        int thirdPartLength = arrayLength - firstPartLength - secondPartLength;

        return
        [
            new ArrayPart(StartIndex: 0, Length: firstPartLength),
            new ArrayPart(StartIndex: firstPartLength, Length: secondPartLength),
            new ArrayPart(
                StartIndex: firstPartLength + secondPartLength,
                Length: thirdPartLength)
        ];
    }

    private static void CreateAndAwaitWorkersRecursively(
        int[] numbers,
        ArrayPart[] parts,
        PartResult?[] results,
        int partIndex)
    {
        // Базовий випадок: для всіх трьох частин потоки вже створено.
        if (partIndex >= parts.Length)
        {
            return;
        }

        int currentPartIndex = partIndex;
        Thread worker = new(() =>
            ProcessPart(numbers, parts[currentPartIndex], results, currentPartIndex))
        {
            Name = $"Part-{currentPartIndex + 1}"
        };

        worker.Start();

        // Рекурсивно створюємо наступний потік до очікування поточного.
        // Тому всі три потоки можуть працювати паралельно.
        CreateAndAwaitWorkersRecursively(numbers, parts, results, partIndex + 1);

        // Під час розкручування рекурсії чекаємо завершення потоків.
        worker.Join();
    }

    private static void ProcessPart(
        int[] numbers,
        ArrayPart part,
        PartResult?[] results,
        int partIndex)
    {
        Stopwatch timer = Stopwatch.StartNew();
        long sum = 0;

        int endIndex = part.StartIndex + part.Length;
        for (int index = part.StartIndex; index < endIndex; index++)
        {
            sum += numbers[index];
        }

        timer.Stop();

        // Кожний потік записує лише у власну комірку, тому lock не потрібен.
        results[partIndex] = new PartResult(
            sum,
            Thread.CurrentThread.ManagedThreadId,
            timer.Elapsed);
    }

    private static long CalculateExpectedSum(int length)
    {
        long completeCycles = length / MaxValue;
        int remainingValues = length % MaxValue;
        long oneCycleSum = (long)MaxValue * (MaxValue + 1) / 2;
        long remainderSum = (long)remainingValues * (remainingValues + 1) / 2;

        return completeCycles * oneCycleSum + remainderSum;
    }

    private static void PrintReport(
        int[] numbers,
        ArrayPart[] parts,
        PartResult?[] results,
        long totalSum,
        long expectedSum,
        TimeSpan parallelTime,
        TimeSpan programTime,
        TimeSpan processorTime)
    {
        Console.WriteLine("Лабораторна робота №1 — багатопотокове додавання чисел");
        Console.WriteLine(new string('=', 67));
        Console.WriteLine($"Розмір обмеженого масиву: {numbers.Length:N0} елементів");
        Console.WriteLine($"Правило заповнення: значення від 1 до {MaxValue} циклічно");
        Console.WriteLine($"Перші 15 значень: {string.Join(", ", numbers.Take(15))}");
        Console.WriteLine($"Кількість доступних логічних процесорів: {Environment.ProcessorCount}");
        Console.WriteLine();

        for (int index = 0; index < parts.Length; index++)
        {
            ArrayPart part = parts[index];
            PartResult result = results[index]!;
            int firstPosition = part.StartIndex + 1;
            int lastPosition = part.StartIndex + part.Length;

            Console.WriteLine($"Частина {index + 1}:");
            Console.WriteLine($"  позиції масиву: {firstPosition:N0}..{lastPosition:N0}");
            Console.WriteLine($"  кількість елементів: {part.Length:N0}");
            Console.WriteLine($"  керований ID потоку: {result.ManagedThreadId}");
            Console.WriteLine($"  сума: {result.Sum:N0}");
            Console.WriteLine($"  час опрацювання: {result.Elapsed.TotalMilliseconds:F3} мс");
        }

        Console.WriteLine();
        Console.WriteLine($"Загальна сума: {totalSum:N0}");
        Console.WriteLine($"Контрольна сума: {expectedSum:N0}");
        Console.WriteLine($"Перевірка: {(totalSum == expectedSum ? "УСПІШНО" : "ПОМИЛКА")}");
        Console.WriteLine($"Час паралельної ділянки: {parallelTime.TotalMilliseconds:F3} мс");
        Console.WriteLine($"Загальний реальний час програми: {programTime.TotalMilliseconds:F3} мс");
        Console.WriteLine($"Сумарний процесорний час процесу: {processorTime.TotalMilliseconds:F3} мс");
    }

    private readonly record struct ArrayPart(int StartIndex, int Length);

    private sealed record PartResult(long Sum, int ManagedThreadId, TimeSpan Elapsed);
}
