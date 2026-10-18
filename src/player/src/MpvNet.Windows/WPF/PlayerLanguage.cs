using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AnimeJaNai.Localization;
using MpvNet.Windows.UI;
namespace MpvNet.Windows.WPF;
public sealed class TextExtension : MarkupExtension
{
    public string Key {get;set;}="";
    public override object ProvideValue(IServiceProvider serviceProvider)=>UiText.Key(Key);
}
public static class LanguagePanel
{
    public static void Attach(Window window)
    {
        var original=(UIElement)window.Content;window.Content=null;
        var hint=new TextBlock {Text=UiText.T("Restart the player and manager to apply the language."),TextWrapping=TextWrapping.Wrap,FontSize=12,Opacity=0.75};
        var select=new ComboBox {Name="InterfaceLanguageSelector",Width=215,
            Foreground=Theme.Current?.Foreground ?? SystemColors.ControlTextBrush,
            Background=Theme.Current?.Background ?? SystemColors.ControlBrush,
            ItemsSource=new[]{"简体中文","English","跟随系统 / System"},SelectedIndex=Array.IndexOf(InterfaceLanguage.Choices,InterfaceLanguage.Read())};
        select.SelectionChanged+=(_,_)=>{
            if(select.SelectedIndex<0)return;
            try {InterfaceLanguage.Save(InterfaceLanguage.Choices[select.SelectedIndex]);hint.Text=UiText.T("Restart the player and manager to apply the language.");}
            catch(Exception e){hint.Text=UiText.T("Could not save language: ")+e.Message;}
        };
        var top=new StackPanel {Margin=new Thickness(14,8,14,4)};
        var row=new StackPanel {Orientation=Orientation.Horizontal};
        row.Children.Add(new TextBlock {Text="界面语言 / Interface language",Margin=new Thickness(0,0,12,0),VerticalAlignment=VerticalAlignment.Center});row.Children.Add(select);
        top.Children.Add(row);top.Children.Add(hint);
        var root=new DockPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);root.Children.Add(original);window.Content=root;
    }
}
