using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.ViewModels;
using Avalonia.Threading;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

internal static class PagingChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Row(int number, string? title = null) => JsonSerializer.Serialize(new
    {
        title = title ?? $"第 {number} 次观看",
        path = @"D:\影视\同一集.mkv",
        // Completion/append order must not require sorting every start time.
        time = 1_792_140_000 - number,
        end_time = 1_792_150_000 + number,
        start = 300,
        end = 1200,
        duration = 1440
    }, JsonOptions);

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("History page did not finish loading");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    public static void Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "watch_history.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, 10_000).Select(number => Row(number)), new UTF8Encoding(false));
        var snapshot = PlaybackHistory.CreateSnapshot(path);
        var page = PlaybackHistory.ReadPage(snapshot, snapshot.Length);
        check(PlaybackHistory.PageSize == 20 && page.Items.Count == 20 && page.NextCursor.HasValue,
            "ten thousand sessions start with a bounded twenty-item page");
        check(page.NextCursor > snapshot.Length * 0.99,
            "the first page cursor consumes only the newest records");
        int count = 0, pages = 0;
        while (true)
        {
            if (page.Items.Count != 20) throw new Exception("A full history page lost an entry");
            foreach (var item in page.Items)
            {
                if (item.Title != $"第 {9_999 - count} 次观看")
                    throw new Exception("Pagination duplicated, skipped or reordered an entry");
                count++;
            }
            pages++;
            if (page.NextCursor is not long next) break;
            page = PlaybackHistory.ReadPage(snapshot, next);
        }
        check(count == 10_000 && pages == 500, "all ten thousand records span five hundred pages without duplicates or gaps");

        var vm = new PlaybackHistoryViewModel(path);
        Wait(vm.RefreshAsync());
        var firstPage = vm.Items.Select(item => item.Title).ToArray();
        File.AppendAllText(path, Row(10_000) + "\n", new UTF8Encoding(false));
        Wait(vm.NextPageAsync());
        check(vm.Items.Count == 20 && vm.Items[0].Title == "第 9979 次观看" && vm.CanPreviousPage,
            "an append does not shift the existing snapshot's next page");
        Wait(vm.PreviousPageAsync());
        check(vm.Items.Select(item => item.Title).SequenceEqual(firstPage) && !vm.CanPreviousPage,
            "going back restores exactly the previous page after an append");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Wait(vm.NextPageAsync());
            check(vm.HasError && vm.Items.Select(item => item.Title).SequenceEqual(firstPage) && !vm.CanPreviousPage,
                "a page read failure retains its previous records and navigation cursor");
        }
        Wait(vm.RefreshAsync());
        check(vm.Items[0].Title == "第 10000 次观看" && !vm.HasError && !vm.CanPreviousPage,
            "refresh includes appended sessions and returns to the first page");
        Wait(vm.NextPageAsync());
        File.WriteAllText(path, Row(-1) + "\n", new UTF8Encoding(false));
        Wait(vm.NextPageAsync());
        check(vm.Items.Count == 1 && vm.Items[0].Title == "第 -1 次观看" && !vm.HasError &&
              !vm.CanPreviousPage && !vm.CanNextPage && !vm.HasPages,
            "a truncated file resets old cursors to the new first page");

        string longTitle = string.Concat(Enumerable.Repeat("片段🎬中文", 1500));
        var records = Enumerable.Range(0, 43).Select(number => Row(number, number == 30 ? longTitle : null)).ToList();
        records.Insert(12, "broken row");
        string pending = Row(43);
        int split = pending.Length / 2;
        File.WriteAllText(path, string.Join("\r\n", records) + "\r\n" + pending[..split], new UTF8Encoding(false));
        snapshot = PlaybackHistory.CreateSnapshot(path);
        page = PlaybackHistory.ReadPage(snapshot, snapshot.Length);
        check(page.Items.Count == 20 && page.Items[0].Title == "第 42 次观看" &&
              page.Items.Any(item => item.Title == longTitle),
            "UTF-8 characters and long records survive reverse block boundaries and CRLF");
        File.AppendAllText(path, pending[split..] + "\r\n", new UTF8Encoding(false));
        var titles = page.Items.Select(item => item.Title).ToList();
        while (page.NextCursor is long next)
        {
            page = PlaybackHistory.ReadPage(snapshot, next);
            titles.AddRange(page.Items.Select(item => item.Title));
        }
        var expected = Enumerable.Range(0, 43).Reverse()
            .Select(number => number == 30 ? longTitle : $"第 {number} 次观看");
        check(titles.SequenceEqual(expected),
            "damaged rows are skipped and completing a tail cannot change an active snapshot");
        snapshot = PlaybackHistory.CreateSnapshot(path);
        page = PlaybackHistory.ReadPage(snapshot, snapshot.Length);
        check(page.Items[0].Title == "第 43 次观看",
            "the completed tail appears exactly once in a refreshed snapshot");
        File.WriteAllText(path, "{\"partial\":", new UTF8Encoding(false));
        snapshot = PlaybackHistory.CreateSnapshot(path);
        check(snapshot.Length == 0 && PlaybackHistory.ReadPage(snapshot, 0).Items.Count == 0,
            "a file containing only an unfinished row stays empty");
    }
}
