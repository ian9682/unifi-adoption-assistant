#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and init-only properties compile on .NET Framework.</summary>
    internal static class IsExternalInit
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }
}
#endif

namespace UniFiAdoption.Core
{
    using System.Net.Sockets;

    /// <summary>Small helpers so the same code builds on .NET Framework 4.8 and modern .NET.</summary>
    internal static class Compat
    {
        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        public static bool Has(this string text, string value) => text.IndexOf(value, StringComparison.Ordinal) >= 0;

        public static bool HasIgnoreCase(this string text, string value) => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Awaits a task with a time limit; on timeout runs <paramref name="onTimeout"/> (for example closing a socket).</summary>
        public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken, Action? onTimeout = null)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(timeout, cts.Token);
                var finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
                if (finished != task)
                {
                    onTimeout?.Invoke();
                    Observe(task);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException();
                }

                cts.Cancel();
                return await task.ConfigureAwait(false);
            }
        }

        public static Task WithTimeout(Task task, TimeSpan timeout, CancellationToken cancellationToken, Action? onTimeout = null) =>
            WithTimeout(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default), timeout, cancellationToken, onTimeout);

        /// <summary>Prevents unobserved-exception noise from a task we stopped waiting for.</summary>
        public static void Observe(Task task) =>
            task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        public static void ReplaceFile(string source, string destination)
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }

        public static IEnumerable<T> DistinctByKey<T, TKey>(this IEnumerable<T> items, Func<T, TKey> key)
        {
            var seen = new HashSet<TKey>();
            foreach (var item in items)
            {
                if (seen.Add(key(item)))
                {
                    yield return item;
                }
            }
        }

        public static bool IsConnectionReset(this SocketException ex) => ex.SocketErrorCode == SocketError.ConnectionReset;
    }
}
