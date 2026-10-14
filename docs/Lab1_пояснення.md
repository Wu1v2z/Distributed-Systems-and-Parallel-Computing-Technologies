# Лабораторна робота №1

## Тема

Організація багатопотокового додавання чисел обмеженого масиву з рекурсивним створенням потоків.

## Мета роботи

Навчитися:

- ділити задачу на незалежні частини;
- створювати та синхронізувати потоки;
- застосовувати рекурсію для організації багатопоточності;
- вимірювати час роботи окремих потоків і всієї програми;
- перевіряти правильність паралельного результату.

## Постановка задачі

Потрібно створити обмежений масив чисел, довільно поділити його на три частини, паралельно виконати додавання чисел кожної частини та знайти загальну суму. Потоки мають створюватися за допомогою рекурсії. Для кожної частини й усієї програми необхідно виміряти час роботи.

## Обрані дані

- мова програмування: C#;
- платформа: .NET 8;
- довжина масиву: `9 000 000` елементів;
- тип елементів: `int`;
- значення: числа від `1` до `100`, які циклічно повторюються;
- тип часткових і загальної сум: `long`, щоб уникнути переповнення;
- поділ масиву: 20 %, 30 % і 50 %.

Отже, частини мають такі розміри:

| Частина | Позиції масиву (від 1) | Кількість елементів | Частка |
|---:|---:|---:|---:|
| 1 | 1–1 800 000 | 1 800 000 | 20 % |
| 2 | 1 800 001–4 500 000 | 2 700 000 | 30 % |
| 3 | 4 500 001–9 000 000 | 4 500 000 | 50 % |

Очікувана сума одного циклу `1 + 2 + ... + 100 = 5050`. У масиві є `90 000` повних циклів, тому контрольна сума дорівнює:

```text
5050 × 90 000 = 454 500 000
```

## Структура програми

### `CreateBoundedArray`

Створює масив фіксованої довжини. Значення обчислюється формулою:

```csharp
numbers[index] = index % MaxValue + 1;
```

Тому дані детерміновані: кожний запуск використовує той самий масив.

### `SplitIntoThreeParts`

Обчислює межі трьох частин. Поділ навмисно нерівномірний, щоб у результатах було видно залежність часу від обсягу роботи.

### `CreateAndAwaitWorkersRecursively`

Це головний метод організації багатопоточності. Він виконує такі дії:

1. створює потік для поточної частини;
2. запускає його методом `Start()`;
3. рекурсивно викликає себе для наступної частини;
4. після повернення з рекурсії очікує потік методом `Join()`.

Базовий випадок рекурсії настає, коли індекс дорівнює кількості частин. Важливо, що `Join()` викликається після рекурсивного створення наступного потоку. Отже, спочатку запускаються всі три потоки, і лише потім програма починає очікувати їх завершення. Завдяки цьому вони можуть працювати паралельно.

### `ProcessPart`

Окремий потік проходить лише свій діапазон масиву, обчислює часткову суму та вимірює час за допомогою `Stopwatch`.

Кожний потік записує результат у власний елемент масиву `results`. Спільного запису в одну змінну немає, тому `lock` не потрібний. Метод `Join()` гарантує, що головний потік читає результати лише після завершення працівників.

### `CalculateExpectedSum`

Обчислює контрольну суму математично, без повторного проходу всього масиву. Це дозволяє перевірити правильність багатопотокового результату.

## Лістинг програми

```csharp
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
```

## Вимірювання часу

Програма показує три різні види часу:

- **час опрацювання частини** — реальний час роботи відповідного потоку, виміряний `Stopwatch`;
- **час паралельної ділянки** — від запуску рекурсивного створення потоків до завершення всіх трьох;
- **загальний реальний час програми** — включає створення масиву, поділ, паралельне обчислення та зведення результату;
- **сумарний процесорний час процесу** — значення `Process.TotalProcessorTime`, тобто скільки процесорного часу витратив процес на всіх ядрах.

Часи окремих частин не потрібно додавати для отримання паралельного часу, оскільки потоки працюють одночасно. Показники можуть трохи відрізнятися між запусками через планувальник операційної системи й поточне навантаження комп'ютера.

## Алгоритм

1. Запустити таймер усієї програми.
2. Створити обмежений масив.
3. Поділити його на три діапазони.
4. Рекурсивно створити та запустити три потоки.
5. У кожному потоці обчислити суму своєї частини та час роботи.
6. За допомогою `Join()` дочекатися всіх потоків.
7. Додати три часткові суми.
8. Порівняти результат із математичною контрольною сумою.
9. Вивести дані, межі частин, ID потоків, суми та часові показники.

## Відповідність завданню

| Вимога | Реалізація |
|---|---|
| Обрати обмежений масив | `int[9_000_000]`, значення 1–100 |
| Розподілити на 3 частини | 20 %, 30 % і 50 % |
| Виконати операцію з кожною частиною | додавання елементів у трьох потоках |
| Організувати паралельне виконання | клас `Thread`, методи `Start()` і `Join()` |
| Використати рекурсію | `CreateAndAwaitWorkersRecursively` |
| Виміряти час | `Stopwatch` та `Process.TotalProcessorTime` |
| Показати результати | консольний звіт для кожної частини та всієї програми |

## Результат контрольного запуску

Після успішної збірки виконано реальний запуск програми. Отримано такі часткові суми:

| Частина | Сума | Час опрацювання |
|---:|---:|---:|
| 1 | 90 900 000 | 3,587 мс |
| 2 | 136 350 000 | 6,174 мс |
| 3 | 227 250 000 | 8,872 мс |

Підсумок контрольного запуску:

```text
Загальна сума: 454 500 000
Контрольна сума: 454 500 000
Перевірка: УСПІШНО
Час паралельної ділянки: 24,341 мс
Загальний реальний час програми: 59,418 мс
Сумарний процесорний час процесу: 46,875 мс
```

Це знімок одного запуску на комп'ютері з 12 логічними процесорами. Часові значення під час наступних запусків змінюватимуться, але суми повинні залишатися однаковими.

## Як запустити у VS Code

1. Відкрити корінь репозиторію у VS Code.
2. Відкрити **Run and Debug** (`Ctrl+Shift+D`).
3. Вибрати **«ЛР1 — багатопотокова сума»**.
4. Натиснути `F5`.

Альтернативний запуск у вбудованому терміналі:

```powershell
dotnet run --project .\lab1\Lab1.csproj
```

## Типові питання на захисті

### Чому використано три потоки?

Тому що за умовою масив поділяється на три частини. Кожний потік незалежно обробляє одну частину.

### Де саме використано рекурсію?

У методі `CreateAndAwaitWorkersRecursively`. Кожний його виклик запускає один потік і рекурсивно переходить до наступної частини.

### Чому потоки справді можуть працювати паралельно?

Очікування через `Join()` відбувається лише після рекурсивного виклику. Тому до початку очікування вже запущено всі три потоки. Якщо процесор має кілька ядер, операційна система може виконувати їх одночасно; на одному ядрі виконання буде квазіпаралельним.

### Навіщо потрібен `Join()`?

`Join()` блокує потік, який його викликав, доки робочий потік не завершиться. Без нього головна програма могла б прочитати ще неготові результати.

### Чому не потрібен `lock`?

Кожний потік читає спільний масив, але записує результат тільки у власну комірку `results[partIndex]`. Конфліктного запису немає. Після `Join()` усі записи гарантовано завершені.

### Чому сума має тип `long`?

Навіть якщо окремі елементи мають тип `int`, сума великої кількості значень може перевищити діапазон `int`. Тип `long` істотно зменшує ризик переповнення.

### Чим реальний час відрізняється від процесорного?

Реальний час показує, скільки минуло за годинником. Процесорний час показує сумарну роботу процесора. У багатопотоковій програмі кілька ядер можуть одночасно накопичувати процесорний час, тому він може відрізнятися від реального.

## Висновок

У роботі масив було поділено на три незалежні частини, для яких рекурсивно створено окремі потоки. Потоки паралельно обчислюють часткові суми, а головна програма після синхронізації формує загальний результат. Контрольна сума підтверджує правильність обчислення, а часові показники демонструють особливості паралельного виконання.
