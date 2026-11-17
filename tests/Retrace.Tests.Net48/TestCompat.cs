// APIs the test sources use that .NET Framework lacks (the plugin's own list is src/Retrace/Compat/NetFramework.cs, linked in here too).
using System.Text.RegularExpressions;

namespace System
{
    static class TestCompat
    {
        extension(Array)
        {
            public static void Fill<T>(T[] array, T value) { for (int i = 0; i < array.Length; i++) array[i] = value; }
        }

        extension(IO.Directory)
        {
            public static IO.DirectoryInfo CreateTempSubdirectory(string? prefix = null) =>
                IO.Directory.CreateDirectory(IO.Path.Combine(IO.Path.GetTempPath(), (prefix ?? "") + Guid.NewGuid().ToString("N")));
        }

        extension(string)
        {
            public static string Create<TState>(int length, TState state, SpanAction<char, TState> action)
            {
                var buf = new char[length];
                action(buf, state);
                return new string(buf);
            }
        }

        public static async Threading.Tasks.Task<T> WaitAsync<T>(this Threading.Tasks.Task<T> task, TimeSpan timeout)
        {
            if (await Threading.Tasks.Task.WhenAny(task, Threading.Tasks.Task.Delay(timeout)) != task) throw new TimeoutException();
            return await task;
        }

        public static async Threading.Tasks.Task WaitAsync(this Threading.Tasks.Task task, TimeSpan timeout)
        {
            if (await Threading.Tasks.Task.WhenAny(task, Threading.Tasks.Task.Delay(timeout)) != task) throw new TimeoutException();
            await task;
        }

        public static List<Match> ToList(this MatchCollection m) => m.Cast<Match>().ToList();
    }

    delegate void SpanAction<T, TArg>(Span<T> span, TArg arg);
}
