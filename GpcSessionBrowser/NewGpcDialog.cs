using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace GpcSessionBrowser;

public sealed class NewGpcDialog : Window
{
    private readonly TextBox _directoryTextBox = new();
    private readonly CheckBox _yoloCheckBox = new() { Content = "Use YOLO mode (--skip-trust --approval-mode yolo)", IsChecked = true };
    private readonly CheckBox _skipTrustCheckBox = new() { Content = "Trust workspace for this session only (--skip-trust)" };
    private readonly CheckBox _sandboxCheckBox = new() { Content = "Run in sandbox (--sandbox)" };
    private readonly CheckBox _debugCheckBox = new() { Content = "Run in debug mode (--debug)" };
    private readonly TextBox _modelTextBox = new();
    private readonly TextBox _worktreeTextBox = new();
    private readonly TextBox _includeDirectoriesTextBox = new();

    public NewGpcDialog(string initialDirectory)
    {
        Title = "Open GPC";
        Width = 620;
        Height = 430;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        _directoryTextBox.Text = Directory.Exists(initialDirectory)
            ? initialDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Content = BuildContent();
    }

    public GpcLaunchOptions? LaunchOptions { get; private set; }

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

        var okButton = new Button { Content = "Open", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        okButton.Click += (_, _) => Accept();
        var cancelButton = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        root.Children.Add(buttons);

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(panel);

        panel.Children.Add(new TextBlock
        {
            Text = "GPC startup directory",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var directoryRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        directoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        directoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _directoryTextBox.VerticalContentAlignment = VerticalAlignment.Center;
        var directoryTextBox = BuildPlaceholderTextBox(_directoryTextBox, @"Example: C:\Users\joyworkshop\Desktop\ME\gpcExtractor");
        Grid.SetColumn(directoryTextBox, 0);
        directoryRow.Children.Add(directoryTextBox);
        var browseButton = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        browseButton.Click += (_, _) => BrowseDirectory();
        Grid.SetColumn(browseButton, 1);
        directoryRow.Children.Add(browseButton);
        panel.Children.Add(directoryRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Startup options",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(_yoloCheckBox);
        panel.Children.Add(_skipTrustCheckBox);
        panel.Children.Add(_sandboxCheckBox);
        panel.Children.Add(_debugCheckBox);

        panel.Children.Add(BuildLabeledTextBox("Model (--model)", _modelTextBox, "Example: gpt-5.5"));
        panel.Children.Add(BuildLabeledTextBox("Git worktree name (--worktree)", _worktreeTextBox, "Example: feature/session-browser"));
        panel.Children.Add(BuildLabeledTextBox("Additional directories (--include-directories)", _includeDirectoriesTextBox, @"Example: C:\shared\configs;C:\shared\docs"));

        return root;
    }

    private static UIElement BuildLabeledTextBox(string label, TextBox textBox, string placeholder)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        textBox.VerticalContentAlignment = VerticalAlignment.Center;
        panel.Children.Add(BuildPlaceholderTextBox(textBox, placeholder));
        return panel;
    }

    private static Grid BuildPlaceholderTextBox(TextBox textBox, string placeholder)
    {
        var container = new Grid();
        var placeholderText = new TextBlock
        {
            Text = placeholder,
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        void UpdatePlaceholderVisibility()
        {
            placeholderText.Visibility = string.IsNullOrEmpty(textBox.Text) && !textBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        textBox.TextChanged += (_, _) => UpdatePlaceholderVisibility();
        textBox.GotKeyboardFocus += (_, _) => UpdatePlaceholderVisibility();
        textBox.LostKeyboardFocus += (_, _) => UpdatePlaceholderVisibility();
        UpdatePlaceholderVisibility();

        container.Children.Add(textBox);
        container.Children.Add(placeholderText);
        return container;
    }

    private void BrowseDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select GPC startup directory",
            InitialDirectory = Directory.Exists(_directoryTextBox.Text)
                ? _directoryTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog(this) == true)
        {
            _directoryTextBox.Text = dialog.FolderName;
        }
    }

    private void Accept()
    {
        var directory = _directoryTextBox.Text.Trim();
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(this, "Please select an existing startup directory.", "Invalid directory", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LaunchOptions = new GpcLaunchOptions(
            WorkingDirectory: directory,
            Yolo: _yoloCheckBox.IsChecked == true,
            SkipTrust: _skipTrustCheckBox.IsChecked == true,
            Sandbox: _sandboxCheckBox.IsChecked == true,
            Debug: _debugCheckBox.IsChecked == true,
            Model: _modelTextBox.Text,
            WorktreeName: _worktreeTextBox.Text,
            IncludeDirectories: _includeDirectoriesTextBox.Text);
        DialogResult = true;
    }
}
