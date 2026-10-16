using System.Windows;
using MpvNet.Windows.UI;

namespace MpvNet.Windows.WPF;

public partial class SubtitleDeleteWindow : Window
{
    public Theme? Theme => Theme.Current;
    public string FileName { get; }
    public bool DeleteLocal => DeleteFile.IsChecked == true;

    public SubtitleDeleteWindow(string fileName)
    {
        FileName = fileName;
        InitializeComponent();
        DataContext = this;
    }

    void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
