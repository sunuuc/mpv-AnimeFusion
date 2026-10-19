using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;

namespace MpvNet.Windows.WPF.Controls;

public partial class MatchSearchControl : UserControl
{
    SubtitleTitles? _titles;
    CancellationTokenSource? _lookup;
    int _generation;
    bool _aliasesWereOpen;

    public MatchSearchControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => ClearMedia();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MatchSearchControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public void SetMedia(TraktMedia media, CancellationToken lifetime)
    {
        ClearMedia();
        _lookup = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _titles = new(media, _lookup.Token);
    }

    void ClearMedia()
    {
        _generation++;
        AliasesPopup.IsOpen = false;
        _aliasesWereOpen = false;
        _lookup?.Cancel();
        _lookup?.Dispose();
        _lookup = null;
        _titles = null;
        AliasesList.ItemsSource = null;
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            e.Handled = true;
            if (!AliasesPopup.IsOpen) OpenAliases();
            else if (AliasesList.Items.Count > 0)
            {
                if (AliasesList.SelectedIndex < 0) AliasesList.SelectedIndex = 0;
                (AliasesList.ItemContainerGenerator.ContainerFromIndex(AliasesList.SelectedIndex) as ListBoxItem)?.Focus();
            }
        }
        else if (e.Key == Key.Escape && AliasesPopup.IsOpen)
        { e.Handled = true; AliasesPopup.IsOpen = false; }
    }

    void Aliases_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _aliasesWereOpen = AliasesPopup.IsOpen;

    void Aliases_Click(object sender, RoutedEventArgs e)
    {
        bool close = _aliasesWereOpen || AliasesPopup.IsOpen;
        _aliasesWereOpen = false;
        if (close) { AliasesPopup.IsOpen = false; return; }
        OpenAliases();
    }

    async void OpenAliases()
    {
        if (_titles == null || _lookup == null) return;
        var titles = _titles;
        var token = _lookup.Token;
        int generation = ++_generation;
        string keyword = Text.Trim();
        AliasesList.ItemsSource = titles.CachedNames;
        AliasesStatus.Text = "正在匹配别名…";
        AliasesStatus.Visibility = Visibility.Visible;
        AliasesPopup.IsOpen = true;
        SearchBox.Focus();
        try
        {
            var names = await titles.NamesAsync(token);
            if (token.IsCancellationRequested || generation != _generation || !AliasesPopup.IsOpen) return;
            AliasesList.ItemsSource = names;
            AliasesStatus.Text = names.Count == 0 ? "没有匹配的别名" : "";
            AliasesStatus.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AliasesList.SelectedItem = names.FirstOrDefault(name => name.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Terminal.WriteError(error);
            if (token.IsCancellationRequested || generation != _generation || !AliasesPopup.IsOpen) return;
            AliasesList.ItemsSource = titles.CachedNames;
            AliasesStatus.Text = "别名获取失败，请重试";
            AliasesStatus.Visibility = Visibility.Visible;
        }
    }

    void Alias_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AliasesList, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: string name })
        { e.Handled = true; SelectAlias(name); }
    }

    void SelectAlias(string name)
    {
        Text = name;
        AliasesPopup.IsOpen = false;
        SearchBox.Focus();
        SearchBox.CaretIndex = name.Length;
    }

    void Aliases_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        { e.Handled = true; AliasesPopup.IsOpen = false; SearchBox.Focus(); }
        else if (e.Key == Key.Enter && AliasesList.SelectedItem is string name)
        { e.Handled = true; SelectAlias(name); }
    }
}
