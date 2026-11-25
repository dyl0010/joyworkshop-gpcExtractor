using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GpcSessionBrowser;

public sealed class TerminalSettingsDialog : Window
{
    private readonly TextBox _terminalPathTextBox = new();
    private readonly TextBox _configEditorPathTextBox = new();

    public TerminalSettingsDialog(string terminalPath, string configEditorPath)
    {
        Title = "Settings";
        Width = 620;
        Height = 250;
        MinWidth = 520;
        MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        _terminalPathTextBox.Text = terminalPath;
        _configEditorPathTextBox.Text = configEditorPath;
        Content = BuildContent();
    }

    public string? TerminalPath { get; private set; }
    public string? ConfigEditorPath { get; private set; }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var saveButton = new Button { Content = "Save", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        saveButton.Click += (_, _) => Save();
        buttons.Children.Add(saveButton);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 84, IsCancel = true });
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "PowerShell-compatible terminal executable", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Used to start new or resumed GPC sessions and gpc --help.",
            Margin = new Thickness(0, 3, 0, 6)
        });

        var terminalRow = new Grid();
        terminalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        terminalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_terminalPathTextBox, 0);
        terminalRow.Children.Add(_terminalPathTextBox);
        var browseButton = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        browseButton.Click += (_, _) => BrowseTerminal();
        Grid.SetColumn(browseButton, 1);
        terminalRow.Children.Add(browseButton);
        panel.Children.Add(terminalRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Program used to open GPC settings.json",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 3)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Use a PATH command (default: code) or an executable path.",
            Margin = new Thickness(0, 0, 0, 6)
        });

        var editorRow = new Grid();
        editorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_configEditorPathTextBox, 0);
        editorRow.Children.Add(_configEditorPathTextBox);
        var browseEditorButton = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        browseEditorButton.Click += (_, _) => BrowseEditor();
        Grid.SetColumn(browseEditorButton, 1);
        editorRow.Children.Add(browseEditorButton);
        panel.Children.Add(editorRow);
        root.Children.Add(panel);
        return root;
    }

    private void BrowseTerminal()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select terminal executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            FileName = File.Exists(_terminalPathTextBox.Text) ? _terminalPathTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog(this) == true)
        {
            _terminalPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseEditor()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select configuration editor executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            FileName = File.Exists(_configEditorPathTextBox.Text) ? _configEditorPathTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog(this) == true)
        {
            _configEditorPathTextBox.Text = dialog.FileName;
        }
    }

    private void Save()
    {
        var terminalPath = _terminalPathTextBox.Text.Trim();
        if (!File.Exists(terminalPath))
        {
            MessageBox.Show(this, "Please select an existing terminal executable.", "Invalid terminal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var configEditorPath = _configEditorPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(configEditorPath))
        {
            MessageBox.Show(this, "Please provide a program to open the GPC configuration file.", "Invalid configuration editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TerminalPath = terminalPath;
        ConfigEditorPath = configEditorPath;
        DialogResult = true;
    }
}
