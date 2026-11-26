
using System.Windows.Controls;

namespace MpvNet.Windows.WPF;

public class MenuHelp
{
    static readonly (string Name, string[] Aliases)[] Placeholders =
    [
        ("Track", ["轨道"]), ("Chapter", ["Chapters", "章节"]),
        ("Recent Files", ["Recent", "最近打开", "近期文件"]), ("Title", ["Titles", "标题"]),
        ("Profile", ["配置", "配置文件"]), ("Audio Device", ["音频设备"]), ("Custom", ["自定义"])
    ];

    public static string? GetPlaceholderName(string header)
    {
        foreach (var (name, aliases) in Placeholders)
            if (header == name || header == _(name) || aliases.Contains(header))
                return name;
        return null;
    }

    public static MenuItem AddLabel(ItemCollection items, string text)
    {
        var item = new MenuItem { Header = text.Replace("_", "__") };
        items.Add(item);
        return item;
    }

    public static MenuItem? Add(ItemCollection? items, string path)
    {
        string[] parts = path.Split(new[] { " > ", " | " }, StringSplitOptions.RemoveEmptyEntries);

        for (int x = 0; x < parts.Length; x++)
        {
            bool found = false;

            foreach (MenuItem i in items!.OfType<MenuItem>())
            {
                if (x < parts.Length - 1)
                {
                    if ((string)i.Header == parts[x])
                    {
                        found = true;
                        items = i.Items;
                    }
                }
            }

            if (!found)
            {
                if (x == parts.Length - 1)
                {
                    if (parts[x] == "-")
                        items?.Add(new Separator());
                    else
                    {
                        MenuItem item = new MenuItem() { Header = parts[x] };
                        items?.Add(item);
                        items = item.Items;
                        return item;
                    }
                }
                else
                {
                    MenuItem item = new MenuItem() { Header = parts[x] };
                    items?.Add(item);
                    items = item.Items;
                }
            }
        }

        return null;
    }
}
