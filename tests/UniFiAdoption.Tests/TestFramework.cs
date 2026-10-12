using System.Diagnostics;
using System.Reflection;

namespace UniFiAdoption.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class TestAttribute(string? category = null) : Attribute
{
    public string? Category { get; } = category;
}

internal sealed class AssertionException(string message) : Exception(message);

internal static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new AssertionException(message ?? "Expected true.");
        }
    }

    public static void False(bool condition, string? message = null) => True(!condition, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message ?? "Values differ"}. Expected: {expected}. Actual: {actual}.");
        }
    }

    public static T NotNull<T>(T? value, string? message = null)
        where T : class
    {
        return value ?? throw new AssertionException(message ?? "Expected a value, got null.");
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null)
        {
            throw new AssertionException(message ?? $"Expected null, got {value}.");
        }
    }

    public static void Contains(string expectedSubstring, string? actual, string? message = null)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new AssertionException($"{message ?? "Substring not found"}. Expected to find: '{expectedSubstring}' in '{actual}'.");
        }
    }

    public static void DoesNotContain(string unexpected, string? actual, string? message = null)
    {
        if (actual is not null && actual.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new AssertionException($"{message ?? "Unexpected substring found"}: '{unexpected}'.");
        }
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var includeIntegration = args.Contains("--integration");
        var filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => (Type: t, Method: m, Attribute: m.GetCustomAttribute<TestAttribute>())))
            .Where(x => x.Attribute is not null)
            .Where(x => includeIntegration || x.Attribute!.Category != "integration")
            .Where(x => filter is null || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Type.Name).ThenBy(x => x.Method.Name)
            .ToList();

        var failed = 0;
        var watch = Stopwatch.StartNew();
        foreach (var (type, method, _) in tests)
        {
            var name = $"{type.Name}.{method.Name}";
            try
            {
                var instance = method.IsStatic ? null : Activator.CreateInstance(type);
                var result = method.Invoke(instance, null);
                if (result is Task task)
                {
                    await task;
                }

                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
                Console.WriteLine($"  FAIL  {name}\n        {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertionException)
                {
                    Console.WriteLine(inner.StackTrace);
                }
            }
        }

        Console.WriteLine($"\n{tests.Count - failed} passed, {failed} failed, {tests.Count} total ({watch.Elapsed.TotalSeconds:0.0}s).");
        return failed == 0 ? 0 : 1;
    }
}
