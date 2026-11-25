using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GpcSessionBrowser;

public sealed class MainWindow : Window
{
    private readonly string _gpcRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gpc");
    private readonly string _localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private readonly TreeView _sessionTree = new();
    private readonly ListBox _searchResults = new();
    private readonly ListBox _promptList = new();
    private readonly System.Windows.Controls.RichTextBox _promptText = new();
    private readonly TextBox _metadataText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _sessionsSubtitleText = new() { Text = "Scanning sessions..." };
    private readonly TextBlock _promptsSubtitleText = new() { Text = "Select a session" };
    private readonly TextBlock _detailsSubtitleText = new() { Text = "No prompt selected" };
    private readonly Button _toolbarResumeButton = CreateToolbarButton("#", "Resume", "Resume the selected session", isEnabled: false, prefixBrush: Brushes.Firebrick);
    private readonly Button _toolbarCopyCommandButton = CreateToolbarButton("%", "Copy Command", "Copy the selected session's resume command", isEnabled: false);
    private readonly Button _toolbarOpenProjectButton = CreateToolbarButton("+", "Open Project", "Open the selected project's folder", isEnabled: false);
    private readonly TextBox _searchTextBox = new() { Width = 300, VerticalContentAlignment = VerticalAlignment.Center };

    // Menu holder for recent sessions
    private readonly MenuItem _recentSessionsMenu = new() { Header = "Recent sessions" };

    private readonly CancellationTokenSource _scanCancellation = new();

    // Calendar/date-range filter UI and state
    private readonly System.Windows.Controls.Primitives.Popup _calendarPopup = new();
    private readonly System.Windows.Controls.Calendar _calendarControl = new();
    private Dictionary<DateOnly,int> _sessionsByDate = new();
    private DateOnly? _filterStartDate;
    private DateOnly? _filterEndDate;

    // Token heat bar UI
    private readonly StackPanel _tokenHeatBar = new() { Orientation = Orientation.Horizontal, Height = 20, Margin = new Thickness(6,4,6,0) };
    private Dictionary<DateOnly,long> _tokensByDate = new();
    private readonly string _tokenCachePath;
    private TokenCache? _tokenCache;
    private CancellationTokenSource? _promptLoadCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _indexCancellation;
    private SessionSummary? _selectedSession;
    private GpcTerminalSettings _terminalSettings;
    private readonly GpcSearchIndex _searchIndex;
    private readonly string _applicationVersion = GetApplicationVersion();
    private System.Collections.Generic.IReadOnlyList<ProjectGroup> _allGroups = Array.Empty<ProjectGroup>();
    private string _activeSearchQuery = string.Empty;

    public MainWindow()
    {
        _terminalSettings = GpcTerminalSettings.Load(_localApplicationData);
        _searchIndex = new GpcSearchIndex(_localApplicationData);
        _tokenCachePath = Path.Combine(_localApplicationData, "GpcSessionBrowser", "token-cache.json");
        UpdateWindowTitle();
        Width = 1280;
        Height = 780;
        MinWidth = 980;
        MinHeight = 560;
        WindowState = WindowState.Maximized;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));

        Content = BuildLayout();
        InputBindings.Add(new KeyBinding(
            new RelayCommand(OpenNewGpcDialog),
            new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => _searchTextBox.Focus()),
            new KeyGesture(Key.K, ModifierKeys.Control)));
        // Bind Escape to clear date filter (per user request)
        InputBindings.Add(new KeyBinding(
            new RelayCommand(ClearCalendarFilter),
            new KeyGesture(Key.Escape)));

        Loaded += async (_, _) =>
        {
            await LoadSessionSummariesAsync(_scanCancellation.Token);
            RefreshRecentSessionsMenu();
            InitializeCalendarPopup();
            // Load token cache from disk (fast) and render immediately, then start background incremental refresh
            LoadTokenCacheAndStartBackgroundRefresh();
            await LoadGpcVersionAsync();
        };
        Closed += (_, _) =>
        {
            _scanCancellation.Cancel();
            _scanCancellation.Dispose();
            _promptLoadCancellation?.Cancel();
            _promptLoadCancellation?.Dispose();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _indexCancellation?.Cancel();
            _indexCancellation?.Dispose();
        };
    }

    private UIElement BuildLayout()
    {
        var root = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251))
        };

        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        _statusText.Text = "Ready";
        _statusText.Padding = new Thickness(12, 7, 12, 7);
        _statusText.Background = new SolidColorBrush(Color.FromRgb(245, 247, 250));
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99));

        // Add token heat bar above the status text
        var bottomPanel = new DockPanel();
        var heatBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _tokenHeatBar
        };
        // place heat bar above the status text
        DockPanel.SetDock(heatBorder, Dock.Top);
        bottomPanel.Children.Add(heatBorder);

        DockPanel.SetDock(_statusText, Dock.Bottom);
        bottomPanel.Children.Add(_statusText);
        DockPanel.SetDock(bottomPanel, Dock.Bottom);
        root.Children.Add(bottomPanel);

        var grid = new Grid { Margin = new Thickness(10, 0, 10, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        root.Children.Add(grid);

        _sessionTree.SelectedItemChanged += async (_, _) => await OnSessionTreeSelectionChangedAsync();
        var searchResultTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(SearchResultTextBlock))
        };
        _searchResults.ItemTemplate = searchResultTemplate;
        _searchResults.SelectionChanged += async (_, _) => await OnSearchResultSelectedAsync();
        _searchResults.Visibility = Visibility.Collapsed;
        var sessionContent = new Grid();
        sessionContent.Children.Add(_sessionTree);
        sessionContent.Children.Add(_searchResults);
        var sessionsPanel = WrapPanel("Sessions", _sessionsSubtitleText, sessionContent);
        Grid.SetColumn(sessionsPanel, 0);
        grid.Children.Add(sessionsPanel);

        var splitter1 = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(229, 231, 235))
        };
        Grid.SetColumn(splitter1, 1);
        grid.Children.Add(splitter1);

        _promptList.DisplayMemberPath = null;
        _promptList.ItemTemplate = new DataTemplate
        {
            VisualTree = new FrameworkElementFactory(typeof(SearchResultTextBlock))
        };
        _promptList.SelectionChanged += (_, _) => OnPromptSelectionChanged();
        var promptsPanel = WrapPanel("Questions", _promptsSubtitleText, _promptList);
        Grid.SetColumn(promptsPanel, 2);
        grid.Children.Add(promptsPanel);

        var splitter2 = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(229, 231, 235))
        };
        Grid.SetColumn(splitter2, 3);
        grid.Children.Add(splitter2);

        var detailsPanel = BuildDetailsPanel();
        Grid.SetColumn(detailsPanel, 4);
        grid.Children.Add(detailsPanel);

        return root;
    }

    private UIElement BuildToolbar()
    {
        var container = new StackPanel { Orientation = Orientation.Vertical };
        var menu = new Menu
        {
            Background = Brushes.White,
            Padding = new Thickness(0),
            FontSize = 12
        };

        var fileMenu = new MenuItem { Header = "_File" };
        var openGpcItem = new MenuItem
        {
            Header = "_Open GPC...",
            InputGestureText = "Ctrl+O"
        };
        openGpcItem.Click += (_, _) => OpenNewGpcDialog();
        var refreshItem = new MenuItem { Header = "_Refresh" };
        refreshItem.Click += async (_, _) => await LoadSessionSummariesAsync(_scanCancellation.Token);
        var openGpcConfigItem = new MenuItem { Header = "Open GPC _Configuration File" };
        openGpcConfigItem.Click += (_, _) => OpenGpcConfigurationFile();

        var openGpcDirItem = new MenuItem { Header = "Open GPC _Directory" };
        openGpcDirItem.Click += (_, _) => OpenGpcRootDirectory();

        var exitItem = new MenuItem { Header = "E_xit" };
        exitItem.Click += (_, _) => Close();

        fileMenu.Items.Add(openGpcItem);
        fileMenu.Items.Add(openGpcDirItem);
        fileMenu.Items.Add(new Separator());
        fileMenu.Items.Add(refreshItem);
        fileMenu.Items.Add(openGpcConfigItem);

        // Recent sessions placeholder (will be populated at runtime)
        fileMenu.Items.Add(new Separator());
        fileMenu.Items.Add(_recentSessionsMenu);

        fileMenu.Items.Add(new Separator());
        fileMenu.Items.Add(exitItem);

        var sessionMenu = new MenuItem { Header = "_Session" };
        var openSessionItem = new MenuItem { Header = "_Open Resume Shell" };
        openSessionItem.Click += (_, _) => OpenResumeShell();
        var copyCommandItem = new MenuItem { Header = "_Copy Resume Command" };
        copyCommandItem.Click += (_, _) => CopyResumeCommand();
        var openProjectItem = new MenuItem { Header = "Open _Project Directory" };
        openProjectItem.Click += (_, _) => OpenProjectDirectory();
        sessionMenu.Items.Add(openSessionItem);
        sessionMenu.Items.Add(copyCommandItem);
        sessionMenu.Items.Add(openProjectItem);

        var viewMenu = new MenuItem { Header = "_View" };
        var focusSearchItem = new MenuItem { Header = "Focus _Search", InputGestureText = "Ctrl+K" };
        focusSearchItem.Click += (_, _) => _searchTextBox.Focus();
        viewMenu.Items.Add(focusSearchItem);

        // Clear date filter menu item (keyboard: Esc)
        var clearDateFilterItem = new MenuItem { Header = "Clear _Date Filter", InputGestureText = "Esc" };
        clearDateFilterItem.Click += (_, _) => ClearCalendarFilter();
        viewMenu.Items.Add(clearDateFilterItem);

        var settingsMenu = new MenuItem { Header = "_Settings" };
        var terminalSettingsItem = new MenuItem { Header = "Configure _Terminal..." };
        terminalSettingsItem.Click += (_, _) => OpenTerminalSettingsDialog();
        settingsMenu.Items.Add(terminalSettingsItem);
        var rebuildSearchIndexItem = new MenuItem { Header = "_Rebuild Search Index" };
        rebuildSearchIndexItem.Click += async (_, _) => await UpdateSearchIndexAsync(rebuild: true);
        settingsMenu.Items.Add(rebuildSearchIndexItem);

        var helpMenu = new MenuItem { Header = "_Help" };
        var openGpcHelpItem = new MenuItem { Header = "Open GPC --_help" };
        openGpcHelpItem.Click += (_, _) => OpenGpcHelp();
        helpMenu.Items.Add(openGpcHelpItem);

        menu.Items.Add(fileMenu);
        menu.Items.Add(sessionMenu);
        menu.Items.Add(viewMenu);
        menu.Items.Add(settingsMenu);
        menu.Items.Add(helpMenu);

        container.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = menu
        });

        container.Children.Add(BuildQuickAccessToolbar());
        return container;
    }

    private UIElement BuildQuickAccessToolbar()
    {
        var toolBar = new ToolBar
        {
            Background = Brushes.White,
            Padding = new Thickness(5, 3, 5, 3)
        };

        var openGpcButton = CreateToolbarButton("#", "Open GPC", "Open a new GPC session (Ctrl+O)");
        openGpcButton.Click += (_, _) => OpenNewGpcDialog();
        toolBar.Items.Add(openGpcButton);

        var refreshButton = CreateToolbarButton("@", "Refresh", "Rescan GPC sessions");
        refreshButton.Click += async (_, _) => await LoadSessionSummariesAsync(_scanCancellation.Token);
        toolBar.Items.Add(refreshButton);
        toolBar.Items.Add(new Separator());

        toolBar.Items.Add(new TextBlock
        {
            Text = "$ Search:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0)
        });
        _searchTextBox.ToolTip = "Search all indexed historical prompts (Ctrl+K)";
        _searchTextBox.TextChanged += async (_, _) => await SearchPromptsAsync();
        toolBar.Items.Add(BuildSearchTextBoxWithPlaceholder());
        var clearSearchButton = CreateToolbarButton("&", "Clear", "Clear search (Esc)");
        clearSearchButton.Click += (_, _) => ClearSearch();
        toolBar.Items.Add(clearSearchButton);
        toolBar.Items.Add(new Separator());

        _toolbarResumeButton.Click += (_, _) => OpenResumeShell();
        _toolbarCopyCommandButton.Click += (_, _) => CopyResumeCommand();
        _toolbarOpenProjectButton.Click += (_, _) => OpenProjectDirectory();
        toolBar.Items.Add(_toolbarResumeButton);
        toolBar.Items.Add(_toolbarCopyCommandButton);
        toolBar.Items.Add(_toolbarOpenProjectButton);
        toolBar.Items.Add(new Separator());

        var settingsButton = CreateToolbarButton("!", "Settings", "Configure terminal and GPC configuration editor");
        settingsButton.Click += (_, _) => OpenTerminalSettingsDialog();
        // Calendar (date range) filter button will be added to the toolbar items later
        // Calendar filter button (📅)
        var calendarButton = CreateToolbarButton("📅", "Date Filter", "Filter sessions by date range");
        calendarButton.Click += (_, _) => ToggleCalendarPopup(calendarButton);
        toolBar.Items.Add(calendarButton);

        toolBar.Items.Add(settingsButton);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolBar
        };
    }

    private Grid BuildSearchTextBoxWithPlaceholder()
    {
        var container = new Grid { Width = _searchTextBox.Width };
        var placeholderText = new TextBlock
        {
            Text = "Search prompts, sessions, projects... Ctrl+K",
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        void UpdatePlaceholderVisibility()
        {
            placeholderText.Visibility = string.IsNullOrEmpty(_searchTextBox.Text) && !_searchTextBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        _searchTextBox.TextChanged += (_, _) => UpdatePlaceholderVisibility();
        _searchTextBox.GotKeyboardFocus += (_, _) => UpdatePlaceholderVisibility();
        _searchTextBox.LostKeyboardFocus += (_, _) => UpdatePlaceholderVisibility();
        UpdatePlaceholderVisibility();

        container.Children.Add(_searchTextBox);
        container.Children.Add(placeholderText);
        return container;
    }

    private static Button CreateToolbarButton(string prefix, string label, string toolTip, bool isEnabled = true, Brush? prefixBrush = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = prefix,
            Foreground = prefixBrush ?? new SolidColorBrush(Color.FromRgb(55, 65, 81)),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center
        });

        return new Button { Content = content, ToolTip = toolTip, IsEnabled = isEnabled };
    }

    private static Border WrapPanel(string title, TextBlock subtitle, UIElement child)
    {
        var panel = new DockPanel();
        panel.Children.Add(BuildPanelHeader(title, subtitle));
        panel.Children.Add(child);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(216, 222, 233)),
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }

    private static UIElement BuildPanelHeader(string title, TextBlock subtitle)
    {
        var header = new StackPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(250, 252, 255)),
            Margin = new Thickness(0),
            Orientation = Orientation.Vertical
        };

        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
            Margin = new Thickness(10, 8, 10, 1)
        });

        subtitle.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        subtitle.Margin = new Thickness(10, 0, 10, 8);
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(subtitle);

        var headerBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header
        };
        DockPanel.SetDock(headerBorder, Dock.Top);
        return headerBorder;
    }

    private UIElement BuildDetailsPanel()
    {
        var outer = new DockPanel
        {
            Background = Brushes.White
        };

        outer.Children.Add(BuildPanelHeader("Details", _detailsSubtitleText));

        var contentGrid = new Grid { Margin = new Thickness(8) };
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        ConfigureReadOnlyRichTextBox(_promptText, "Select a session, then select a prompt.");
        ConfigureReadOnlyTextBox(_metadataText, "No session selected.");

        Grid.SetRow(_promptText, 0);
        contentGrid.Children.Add(_promptText);

        var splitter = new GridSplitter
        {
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromRgb(229, 231, 235))
        };
        Grid.SetRow(splitter, 1);
        contentGrid.Children.Add(splitter);

        Grid.SetRow(_metadataText, 2);
        contentGrid.Children.Add(_metadataText);

        outer.Children.Add(contentGrid);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(216, 222, 233)),
            BorderThickness = new Thickness(1),
            Child = outer
        };
    }

    private static void ConfigureReadOnlyTextBox(TextBox textBox, string text)
    {
        textBox.Text = text;
        textBox.IsReadOnly = true;
        textBox.AcceptsReturn = true;
        textBox.AcceptsTab = true;
        textBox.TextWrapping = TextWrapping.Wrap;
        textBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        textBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        textBox.FontFamily = new FontFamily("Consolas");
    }

    private static void ConfigureReadOnlyRichTextBox(RichTextBox richTextBox, string text)
    {
        richTextBox.Document = new FlowDocument();
        richTextBox.IsReadOnly = true;
        richTextBox.AcceptsTab = true;
        richTextBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        richTextBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        richTextBox.FontFamily = new FontFamily("Consolas");
        SetRichTextBoxText(richTextBox, text, null);
    }

    private static void SetRichTextBoxText(RichTextBox richTextBox, string text, string? query)
    {
        richTextBox.Document.Blocks.Clear();
        var paragraph = new Paragraph();

        if (string.IsNullOrWhiteSpace(query))
        {
            paragraph.Inlines.Add(new Run(text));
            richTextBox.Document.Blocks.Add(paragraph);
            return;
        }

        var terms = System.Text.RegularExpressions.Regex.Split(query.Trim(), @"\s+");
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var matchIndex = -1;
            var matchLength = 0;
            foreach (var term in terms)
            {
                if (term.Length == 0)
                {
                    continue;
                }

                var candidateIndex = text.IndexOf(term, searchStart, StringComparison.OrdinalIgnoreCase);
                if (candidateIndex >= 0 && (matchIndex < 0 || candidateIndex < matchIndex))
                {
                    matchIndex = candidateIndex;
                    matchLength = term.Length;
                }
            }

            if (matchIndex < 0)
            {
                paragraph.Inlines.Add(new Run(text[searchStart..]));
                break;
            }

            if (matchIndex > searchStart)
            {
                paragraph.Inlines.Add(new Run(text[searchStart..matchIndex]));
            }

            var highlighted = new Run(text.Substring(matchIndex, matchLength))
            {
                Foreground = Brushes.Firebrick,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(254, 226, 226))
            };
            paragraph.Inlines.Add(highlighted);
            searchStart = matchIndex + matchLength;
        }

        richTextBox.Document.Blocks.Add(paragraph);
    }

    private async Task LoadSessionSummariesAsync(CancellationToken cancellationToken)
    {
        SetStatus("Scanning lightweight session summaries...");
        ClearSelection("Scanning sessions. Prompts will be parsed lazily after a session is selected.");

        try
        {
            var groups = await Task.Run(() => GpcSessionDataReader.ScanSessionSummaries(_gpcRoot, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var sessionCount = groups.Sum(group => group.Sessions.Count);
            _sessionsSubtitleText.Text = $"{groups.Count:N0} projects · {sessionCount:N0} sessions";
            SetStatus($"Loaded {sessionCount:N0} sessions from {groups.Count:N0} projects. Select a session to parse prompts.");
            _allGroups = groups;
            PopulateSessionTree(_allGroups);
            _ = UpdateSearchIndexAsync(rebuild: false);

            if (sessionCount == 0)
            {
                SetPromptContent($"No sessions found under {Path.Combine(_gpcRoot, "tmp")}.");
                _sessionsSubtitleText.Text = "No sessions found";
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan cancelled.");
            _sessionsSubtitleText.Text = "Scan cancelled";
        }
        catch (Exception ex)
        {
            SetStatus("Scan failed.");
            _sessionsSubtitleText.Text = "Scan failed";
            SetPromptContent(ex.ToString());
        }
    }

    private void PopulateSessionTree(System.Collections.Generic.IReadOnlyList<ProjectGroup> groups)
    {
        _sessionTree.Items.Clear();
        TreeViewItem? firstSessionItem = null;

        foreach (var group in groups)
        {
            var projectItem = new TreeViewItem
            {
                Header = BuildProjectTreeHeader(group),
                Tag = group,
                IsExpanded = false,
                ToolTip = $"{group.ProjectRoot}{Environment.NewLine}Key: {group.ProjectKey}"
            };

            foreach (var session in group.Sessions)
            {
                var sessionItem = new TreeViewItem
                {
                    Header = session.DisplayName,
                    Tag = session,
                    ToolTip = session.FilePath
                };

                firstSessionItem ??= sessionItem;
                projectItem.Items.Add(sessionItem);
            }

            _sessionTree.Items.Add(projectItem);
        }

        if (firstSessionItem is not null)
        {
            if (ItemsControl.ItemsControlFromItemContainer(firstSessionItem) is TreeViewItem parentProject)
            {
                parentProject.IsExpanded = true;
            }

            firstSessionItem.IsSelected = true;
        }
    }

    private static UIElement BuildProjectTreeHeader(ProjectGroup group)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        header.Children.Add(new TextBlock
        {
            Text = "📁",
            FontSize = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        header.Children.Add(new TextBlock
        {
            Text = $"{group.DisplayName} ({group.Sessions.Count:N0})",
            VerticalAlignment = VerticalAlignment.Center
        });

        return header;
    }

    private async Task OnSessionTreeSelectionChangedAsync()
    {
        if (_sessionTree.SelectedItem is not TreeViewItem item)
        {
            ClearSelection("No session selected.");
            return;
        }

        if (item.Tag is ProjectGroup project)
        {
            _selectedSession = null;
            SetSessionButtonsEnabled(false);
            _promptList.ItemsSource = null;
            SetPromptContent("Select a session under this project to load prompts.");
            _metadataText.Text = BuildProjectMetadata(project);
            _promptsSubtitleText.Text = "Select a session";
            _detailsSubtitleText.Text = $"Project · {project.DisplayName}";
            SetStatus($"Selected project {project.DisplayName}.");

            // Append repository status for the project asynchronously
            _ = Task.Run(async () =>
            {
                try
                {
                    var repoStatus = await GetWorkspaceStatusAsync(project.ProjectRoot, sessionProjectHash: null);
                    Dispatcher.Invoke(() => _metadataText.Text = BuildProjectMetadata(project) + Environment.NewLine + repoStatus);
                }
                catch
                {
                    // ignore
                }
            });

            return;
        }

        if (item.Tag is not SessionSummary session)
        {
            ClearSelection("No session selected.");
            return;
        }

        await LoadSessionPromptsAsync(session);
    }

    private async Task OnSearchResultSelectedAsync()
    {
        if (_searchResults.SelectedItem is PromptSearchResult result)
        {
            await LoadSessionPromptsAsync(result.Session, result.PromptOrder);
        }
    }

    private async Task LoadSessionPromptsAsync(SessionSummary session, int? promptOrder = null)
    {

        _promptLoadCancellation?.Cancel();
        _promptLoadCancellation?.Dispose();
        _promptLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _promptLoadCancellation.Token;

        _selectedSession = session;
        SetSessionButtonsEnabled(true);
        _promptList.ItemsSource = null;
        SetPromptContent("Loading prompts...");
        _metadataText.Text = BuildSessionMetadata(session, promptCount: null);
        _promptsSubtitleText.Text = $"Loading · {session.ProjectKey}";
        _detailsSubtitleText.Text = "Session metadata";
        SetStatus($"Parsing prompts lazily for session {session.SessionId}...");

        try
        {
            var prompts = await Task.Run(() => GpcSessionDataReader.ParsePrompts(session.FilePath, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _promptList.ItemsSource = prompts
                .Select(prompt => prompt with { SearchQuery = _activeSearchQuery })
                .ToArray();
            _metadataText.Text = BuildSessionMetadata(session, prompts.Count);
            _promptsSubtitleText.Text = $"{prompts.Count:N0} prompts · {session.ProjectKey}";

            // record this session as recently opened
            try
            {
                AddToRecentSessions(session);
                RefreshRecentSessionsMenu();
            }
            catch
            {
                // ignore persistence failures
            }
            if (prompts.Count == 0)
            {
                SetPromptContent("No user prompts found in this session.");
                _detailsSubtitleText.Text = "No prompt found";
            }
            else
            {
                _promptList.SelectedIndex = promptOrder.HasValue && promptOrder.Value > 0 && promptOrder.Value <= prompts.Count
                    ? promptOrder.Value - 1
                    : 0;
            }

            // Ensure prompts reflect current search highlighting (if any)
            RefreshPromptHighlighting();

            // Update calendar counts and heatmap
            UpdateSessionsByDateCache();
            UpdateCalendarHeatmapIfVisible();

            // Update token heatmap asynchronously (only update on initial load; avoid recalculating on every prompt selection)
            // _ = UpdateTokenHeatmapAsync(); // disabled to avoid IO on each session select

            // Check workspace git status asynchronously and append to metadata (does not block prompt rendering)
            _ = Task.Run(async () =>
            {
                try
                {
                    var repoStatus = await GetWorkspaceStatusAsync(session.ProjectRoot, session.ProjectHash);
                    // Update UI on dispatcher thread
                    Dispatcher.Invoke(() =>
                    {
                        _metadataText.Text = BuildSessionMetadata(session, prompts.Count) + Environment.NewLine + repoStatus;
                    });
                }
                catch
                {
                    // Ignore workspace status failures
                }
            });

            SetStatus($"Loaded {prompts.Count:N0} prompts for session {session.SessionId}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Prompt parse cancelled.");
        }
        catch (Exception ex)
        {
            SetPromptContent(ex.ToString());
            SetStatus("Prompt parse failed.");
        }
    }

    private void OnPromptSelectionChanged()
    {
        if (_promptList.SelectedItem is PromptRecord prompt)
        {
            SetPromptContent(prompt.Text);
            _detailsSubtitleText.Text = $"Prompt #{prompt.Order:N0} · line {prompt.LineNumber:N0}";
            SetStatus($"Selected prompt #{prompt.Order:N0} from line {prompt.LineNumber:N0}.");
        }
        else if (_selectedSession is not null)
        {
            SetPromptContent("Select a prompt to view its full text.");
            _detailsSubtitleText.Text = "Session metadata";
        }
    }

    private void OpenNewGpcDialog()
    {
        var initialDirectory = _selectedSession?.ProjectRoot
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dialog = new NewGpcDialog(initialDirectory)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.LaunchOptions is null)
        {
            return;
        }

        OpenNewGpc(dialog.LaunchOptions);
    }

    private void OpenNewGpc(GpcLaunchOptions options)
    {
        try
        {
            var commandParts = new[] { "gpc" }
                .Concat(options.BuildArguments().Select(GpcSessionDataReader.QuotePowerShell));
            StartTerminal(string.Join(" ", commandParts), options.WorkingDirectory);
            SetStatus($"Opened new GPC in {options.WorkingDirectory}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open GPC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenGpcHelp()
    {
        try
        {
            StartTerminal("gpc --help", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            SetStatus("Opened gpc --help in the configured terminal.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open GPC help", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Unable to open gpc --help.");
        }
    }

    private void OpenResumeShell()
    {
        if (_selectedSession is null)
        {
            return;
        }

        try
        {
            StartTerminal(
                $"gpc --skip-trust --approval-mode yolo --resume {GpcSessionDataReader.QuotePowerShell(_selectedSession.SessionId)}",
                Directory.Exists(_selectedSession.ProjectRoot)
                    ? _selectedSession.ProjectRoot
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            SetStatus($"Opened resume shell for {_selectedSession.SessionId} with trusted YOLO mode.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open resume shell", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyResumeCommand()
    {
        if (_selectedSession is null)
        {
            return;
        }

        var command = $"Set-Location -LiteralPath {GpcSessionDataReader.QuotePowerShell(_selectedSession.ProjectRoot)}; gpc --skip-trust --approval-mode yolo --resume {GpcSessionDataReader.QuotePowerShell(_selectedSession.SessionId)}";
        Clipboard.SetText(command);
        SetStatus("Copied trusted YOLO resume command to clipboard.");
    }

    private async Task SearchPromptsAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellationToken = _searchCancellation.Token;
        var query = _searchTextBox.Text.Trim();
        _activeSearchQuery = query;
        RefreshPromptHighlighting();

        if (string.IsNullOrWhiteSpace(query))
        {
            _searchResults.ItemsSource = null;
            _searchResults.Visibility = Visibility.Collapsed;
            _sessionTree.Visibility = Visibility.Visible;
            var sessionCount = _allGroups.Sum(group => group.Sessions.Count);
            _sessionsSubtitleText.Text = $"{_allGroups.Count:N0} projects · {sessionCount:N0} sessions";
            return;
        }

        try
        {
            await Task.Delay(300, cancellationToken);
            var results = await _searchIndex.SearchAsync(query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _sessionTree.Visibility = Visibility.Collapsed;
            _searchResults.Visibility = Visibility.Visible;

            // De-duplicate by session: show one result per session (pick the earliest matching prompt order)
            var distinctBySession = results
                .GroupBy(r => r.Session.SessionId)
                .Select(g => g.OrderBy(r => r.PromptOrder).First())
                .ToList();

            _searchResults.ItemsSource = distinctBySession;
            _sessionsSubtitleText.Text = $"{distinctBySession.Count:N0} matching sessions ({results.Count:N0} prompts)";
            SetStatus($"Found {results.Count:N0} indexed prompts across {distinctBySession.Count:N0} sessions matching \"{query}\".");
        }
        catch (OperationCanceledException)
        {
            // Another keystroke superseded this search.
        }
        catch (Exception ex)
        {
            SetStatus("Search failed.");
            SetPromptContent(ex.ToString());
        }
    }

    private async Task UpdateSearchIndexAsync(bool rebuild)
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = new CancellationTokenSource();
        var cancellationToken = _indexCancellation.Token;

        if (_allGroups.Count == 0)
        {
            return;
        }

        try
        {
            SetStatus(rebuild ? "Rebuilding full-text search index..." : "Updating full-text search index...");
            await _searchIndex.UpdateAsync(_allGroups, rebuild, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus("Search index is up to date.");
            if (!string.IsNullOrWhiteSpace(_searchTextBox.Text))
            {
                await SearchPromptsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer scan or rebuild superseded this index operation.
        }
        catch (Exception ex)
        {
            SetStatus("Search index update failed.");
            SetPromptContent(ex.ToString());
        }
    }

    private void ClearSearch()
    {
        if (_searchTextBox.Text.Length > 0)
        {
            _searchTextBox.Clear();
        }
    }

    private void RefreshPromptHighlighting()
    {
        if (_promptList.ItemsSource is not IEnumerable<PromptRecord> prompts)
        {
            return;
        }

        var selectedOrder = (_promptList.SelectedItem as PromptRecord)?.Order;
        var updatedPrompts = prompts
            .Select(prompt => prompt with { SearchQuery = _activeSearchQuery })
            .ToArray();
        _promptList.ItemsSource = updatedPrompts;
        if (selectedOrder.HasValue)
        {
            _promptList.SelectedItem = updatedPrompts.FirstOrDefault(prompt => prompt.Order == selectedOrder.Value);
        }
    }

    private void OpenTerminalSettingsDialog()
    {
        var dialog = new TerminalSettingsDialog(_terminalSettings.TerminalPath, _terminalSettings.ConfigEditorPath) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.TerminalPath is null || dialog.ConfigEditorPath is null)
        {
            return;
        }

        _terminalSettings = new GpcTerminalSettings(dialog.TerminalPath, dialog.ConfigEditorPath);
        try
        {
            _terminalSettings.Save(_localApplicationData);
            SetStatus($"Terminal updated: {_terminalSettings.TerminalPath}");
            _ = LoadGpcVersionAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to save settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartTerminal(string command, string workingDirectory)
    {
        if (!File.Exists(_terminalSettings.TerminalPath))
        {
            throw new FileNotFoundException(
                "The configured terminal executable was not found. Update it in Settings > Configure Terminal.",
                _terminalSettings.TerminalPath);
        }

        var startInfo = new ProcessStartInfo(_terminalSettings.TerminalPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        Process.Start(startInfo);
    }

    private void OpenGpcConfigurationFile()
    {
        var configFilePath = Path.Combine(_gpcRoot, "settings.json");
        if (!File.Exists(configFilePath))
        {
            MessageBox.Show(
                this,
                $"GPC configuration file was not found:{Environment.NewLine}{configFilePath}",
                "GPC configuration not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(_terminalSettings.ConfigEditorPath)
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(configFilePath);
            Process.Start(startInfo);
            SetStatus($"Opened GPC configuration file: {configFilePath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Unable to open the GPC configuration file with {_terminalSettings.ConfigEditorPath}.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Unable to open GPC configuration",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenGpcRootDirectory()
    {
        try
        {
            if (!Directory.Exists(_gpcRoot))
            {
                MessageBox.Show(this, $"GPC directory not found:{Environment.NewLine}{_gpcRoot}", "GPC directory not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { _gpcRoot }
            });
            SetStatus($"Opened GPC directory: {_gpcRoot}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open GPC directory", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadGpcVersionAsync()
    {
        try
        {
            if (!File.Exists(_terminalSettings.TerminalPath))
            {
                UpdateWindowTitle();
                return;
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(_terminalSettings.TerminalPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add("gpc --version");

            process.Start();
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var version = (await standardOutputTask)
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            _ = await standardErrorTask;
            UpdateWindowTitle(process.ExitCode == 0 && !string.IsNullOrWhiteSpace(version) ? version : null);
        }
        catch
        {
            UpdateWindowTitle();
        }
    }

    private void UpdateWindowTitle(string? gpcVersion = null)
    {
        Title = $"GamePilot Session Browser v{_applicationVersion} · GPC {gpcVersion ?? "未知"}";
    }

    private static string GetApplicationVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "未知" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void OpenProjectDirectory()
    {
        if (_selectedSession is null)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_selectedSession.ProjectRoot))
            {
                MessageBox.Show(
                    this,
                    $"Project directory does not exist:{Environment.NewLine}{_selectedSession.ProjectRoot}",
                    "Project directory not found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { _selectedSession.ProjectRoot }
            });
            SetStatus("Opened project directory.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Unable to open project directory", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearSelection(string promptText)
    {
        _selectedSession = null;
        _promptList.ItemsSource = null;
        SetPromptContent(promptText);
        _metadataText.Text = "No session selected.";
        _promptsSubtitleText.Text = "Select a session";
        _detailsSubtitleText.Text = "No prompt selected";
        SetSessionButtonsEnabled(false);
    }

    // Recent sessions persistence (store file paths)
    private string RecentSessionsFilePath => Path.Combine(_localApplicationData, "GpcSessionBrowser", "recent-sessions.json");

    private void AddToRecentSessions(SessionSummary session)
    {
        var list = LoadRecentSessions().ToList();
        // use file path as unique key
        list.RemoveAll(p => string.Equals(p, session.FilePath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, session.FilePath);
        if (list.Count > 5) list = list.Take(5).ToList();
        SaveRecentSessions(list);
    }

    private IReadOnlyList<string> LoadRecentSessions()
    {
        try
        {
            var file = RecentSessionsFilePath;
            if (!File.Exists(file)) return Array.Empty<string>();
            var json = File.ReadAllText(file);
            var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
            return arr ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void SaveRecentSessions(IReadOnlyList<string> paths)
    {
        try
        {
            var dir = Path.GetDirectoryName(RecentSessionsFilePath)!;
            Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(paths);
            File.WriteAllText(RecentSessionsFilePath, json);
        }
        catch
        {
            // ignore
        }
    }

    private void RefreshRecentSessionsMenu()
    {
        Dispatcher.Invoke(() =>
        {
            _recentSessionsMenu.Items.Clear();
            var recent = LoadRecentSessions();
            if (recent.Count == 0)
            {
                var empty = new MenuItem { Header = "(none)", IsEnabled = false };
                _recentSessionsMenu.Items.Add(empty);
                return;
            }

            foreach (var path in recent)
            {
                // try to map file path back to session summary for label
                var session = _allGroups.SelectMany(g => g.Sessions).FirstOrDefault(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));
                var header = session is not null ? $"{session.ProjectKey} · {session.Title ?? session.SessionId}" : Path.GetFileName(path);
                var mi = new MenuItem { Header = header, ToolTip = path };
                mi.Click += async (_, _) =>
                {
                    // try to locate and select the session in the UI
                    var ss = _allGroups.SelectMany(g => g.Sessions).FirstOrDefault(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));
                    if (ss is not null)
                    {
                        // expand project tree and select corresponding TreeViewItem if possible
                        SelectSessionInTree(ss);
                        await LoadSessionPromptsAsync(ss);
                    }
                    else
                    {
                        // fallback: attempt to load prompts directly if file exists
                        if (File.Exists(path))
                        {
                            var summary = new SessionSummary(SessionId: Path.GetFileNameWithoutExtension(path), ProjectKey: "(unknown)", ProjectRoot: Path.GetDirectoryName(path) ?? string.Empty, FilePath: path, ProjectHash: null, Kind: null, StartTime: null, LastUpdated: null, FileSizeBytes: new FileInfo(path).Length, Title: null);
                            await LoadSessionPromptsAsync(summary);
                        }
                        else
                        {
                            MessageBox.Show(this, $"Session file not found: {path}", "Recent session not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                };

                _recentSessionsMenu.Items.Add(mi);
            }
        });
    }

    private void SelectSessionInTree(SessionSummary session)
    {
        // expand project nodes and select the TreeViewItem whose Tag == session
        foreach (var projObj in _sessionTree.Items)
        {
            if (projObj is not TreeViewItem projItem) continue;
            foreach (var child in projItem.Items)
            {
                if (child is not TreeViewItem childItem) continue;
                if (childItem.Tag is SessionSummary s && string.Equals(s.FilePath, session.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    projItem.IsExpanded = true;
                    childItem.IsSelected = true;
                    childItem.BringIntoView();
                    return;
                }
            }
        }
    }

    private static IReadOnlyList<ProjectGroup> GetFilteredGroupsByDate(IReadOnlyList<ProjectGroup> groups, DateOnly? start, DateOnly? end)
    {
        if (start is null || end is null)
        {
            return groups;
        }

        var result = new List<ProjectGroup>();
        foreach (var g in groups)
        {
            var sessions = g.Sessions.Where(s => s.LastUpdated.HasValue &&
                DateOnly.FromDateTime(s.LastUpdated.Value.LocalDateTime.Date) >= start.Value &&
                DateOnly.FromDateTime(s.LastUpdated.Value.LocalDateTime.Date) <= end.Value).ToArray();
            if (sessions.Length > 0)
            {
                result.Add(new ProjectGroup(g.ProjectKey, g.ProjectRoot, sessions));
            }
        }

        return result;
    }

    private async Task UpdateTokenHeatmapAsync()
    {
        // legacy: full recompute on demand (kept for manual rebuilds)
        try
        {
            SetStatus("Computing token heatmap...");
            var dict = await Task.Run(() => GpcSessionDataReader.AggregateTokenUsageByDate(_allGroups, CancellationToken.None));

            if (dict == null || dict.Count == 0 || dict.Values.All(v => v == 0))
            {
                SetStatus("Estimating token usage from prompt text (fallback)...");
                dict = await Task.Run(() => GpcSessionDataReader.EstimateTokenUsageByDate(_allGroups, CancellationToken.None));
            }

            Dispatcher.Invoke(() =>
            {
                _tokensByDate = dict;
                RenderTokenHeatBar();
                SetStatus("Ready");
            });
        }
        catch
        {
            Dispatcher.Invoke(() => SetStatus("Ready"));
        }
    }

    // Load cache quickly on startup and start a background incremental refresh so UI is not blocked
    private void LoadTokenCacheAndStartBackgroundRefresh()
    {
        try
        {
            var cache = TokenCache.LoadFromFile(_tokenCachePath);
            _tokenCache = cache;
            if (cache != null && cache.Aggregate != null && cache.Aggregate.Count > 0)
            {
                // convert to DateOnly dictionary
                var dict = new Dictionary<DateOnly, long>();
                foreach (var kv in cache.Aggregate)
                {
                    if (DateOnly.TryParse(kv.Key, out var day)) dict[day] = kv.Value;
                }

                _tokensByDate = dict;
                RenderTokenHeatBar();
                var age = cache.CachedAt == DateTimeOffset.MinValue ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - cache.CachedAt;
                SetStatus($"Heatmap loaded from cache — {age.TotalHours:F1}h old");
            }
            else
            {
                SetStatus("No token cache found; computing in background...");
            }
        }
        catch
        {
            // ignore cache read errors
            SetStatus("Starting (no cache)");
        }

        // Start background refresh (fire-and-forget)
        _ = Task.Run(async () => await BackgroundRefreshTokenCacheAsync());
    }

    private async Task BackgroundRefreshTokenCacheAsync()
    {
        try
        {
            // Build current session map from loaded summaries
            var currentSessions = _allGroups.SelectMany(g => g.Sessions).ToList();

            var cache = _tokenCache ?? new TokenCache { CachedAt = DateTimeOffset.MinValue };
            var updated = false;

            // detect removed sessions in cache
            var cachedSessionIds = new HashSet<string>(cache.Sessions.Keys);
            var currentIds = new HashSet<string>(currentSessions.Select(s => s.SessionId));
            foreach (var removedId in cachedSessionIds.Except(currentIds).ToList())
            {
                // subtract from aggregate
                if (cache.Sessions.TryGetValue(removedId, out var entry))
                {
                    foreach (var kv in entry.PerDayTokens)
                    {
                        cache.Aggregate[kv.Key] = cache.Aggregate.GetValueOrDefault(kv.Key) - kv.Value;
                        if (cache.Aggregate[kv.Key] <= 0) cache.Aggregate.Remove(kv.Key);
                    }
                }

                cache.Sessions.Remove(removedId);
                updated = true;
            }

            // find sessions that need update (new or lastWriteUtc changed)
            var toUpdate = new List<SessionSummary>();
            foreach (var s in currentSessions)
            {
                DateTimeOffset lastWriteUtc = s.LastUpdated?.UtcDateTime ?? File.GetLastWriteTimeUtc(s.FilePath);
                if (!cache.Sessions.TryGetValue(s.SessionId, out var existing))
                {
                    toUpdate.Add(s); // new
                    continue;
                }

                if (lastWriteUtc > existing.LastWriteUtc)
                {
                    toUpdate.Add(s); // changed
                }
            }

            if (toUpdate.Count == 0 && cache.Aggregate.Count > 0)
            {
                _tokenCache = cache;
                // still update cachedAt
                cache.CachedAt = DateTimeOffset.UtcNow;
                TokenCache.SaveToFileAtomic(cache, _tokenCachePath);
                return;
            }

            // Process updates sequentially to limit IO; could be parallelized if needed
            foreach (var s in toUpdate)
            {
                try
                {
                    var per = GpcSessionDataReader.ComputeTokenUsageForSession(s, CancellationToken.None);
                    // convert per keys to string
                    var perStr = new Dictionary<string, long>();
                    foreach (var kv in per)
                    {
                        perStr[kv.Key.ToString("yyyy-MM-dd")] = kv.Value;
                    }

                    // subtract old values if present
                    if (cache.Sessions.TryGetValue(s.SessionId, out var oldEntry))
                    {
                        foreach (var kv in oldEntry.PerDayTokens)
                        {
                            cache.Aggregate[kv.Key] = cache.Aggregate.GetValueOrDefault(kv.Key) - kv.Value;
                            if (cache.Aggregate[kv.Key] <= 0) cache.Aggregate.Remove(kv.Key);
                        }
                    }

                    // add new per-day tokens
                    foreach (var kv in perStr)
                    {
                        cache.Aggregate[kv.Key] = cache.Aggregate.GetValueOrDefault(kv.Key) + kv.Value;
                    }

                    cache.Sessions[s.SessionId] = new SessionEntry
                    {
                        FilePath = s.FilePath,
                        LastWriteUtc = s.LastUpdated?.UtcDateTime ?? File.GetLastWriteTimeUtc(s.FilePath),
                        PerDayTokens = perStr
                    };

                    updated = true;
                }
                catch
                {
                    // ignore per-session failures
                }
            }

            if (updated)
            {
                cache.CachedAt = DateTimeOffset.UtcNow;
                TokenCache.SaveToFileAtomic(cache, _tokenCachePath);
                _tokenCache = cache;

                // update UI
                var dict = new Dictionary<DateOnly, long>();
                foreach (var kv in cache.Aggregate)
                {
                    if (DateOnly.TryParse(kv.Key, out var day)) dict[day] = kv.Value;
                }

                Dispatcher.Invoke(() =>
                {
                    _tokensByDate = dict;
                    RenderTokenHeatBar();
                    SetStatus("Heatmap updated");
                });
            }
        }
        catch
        {
            // ignore background errors
            Dispatcher.Invoke(() => SetStatus("Ready"));
        }
    }

    private void RenderTokenHeatBar()
    {
        _tokenHeatBar.Children.Clear();

        if (_allGroups.Count == 0)
        {
            return;
        }

        // Determine overall date span from sessions
        var dates = _allGroups.SelectMany(g => g.Sessions).Where(s => s.LastUpdated.HasValue).Select(s => DateOnly.FromDateTime(s.LastUpdated.Value.LocalDateTime.Date)).ToArray();
        if (dates.Length == 0)
        {
            return;
        }

        var min = dates.Min();
        var max = dates.Max();
        var totalDays = (int)(max.ToDateTime(TimeOnly.MinValue) - min.ToDateTime(TimeOnly.MinValue)).TotalDays + 1;
        if (totalDays <= 0) totalDays = 1;

        var maxTokens = _tokensByDate.Count == 0 ? 0L : _tokensByDate.Values.Max();
        double availableWidth = this.ActualWidth > 0 ? this.ActualWidth - 40 : 800; // fallback
        var rectWidth = Math.Max(2, (int)(availableWidth / totalDays));

        // If there are too many days, bucket them to avoid creating thousands of UI elements.
        var maxRects = 1000;
        if (totalDays <= maxRects)
        {
            for (var i = 0; i < totalDays; i++)
            {
                var day = min.AddDays(i);
                var count = _tokensByDate.GetValueOrDefault(day);
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = rectWidth,
                    Height = _tokenHeatBar.Height,
                    Margin = new Thickness(0.5, 0, 0.5, 0),
                    Fill = new SolidColorBrush(Color.FromRgb(245, 245, 247))
                };

                if (count > 0 && maxTokens > 0)
                {
                    var t = (double)count / (double)maxTokens;
                    var r = (byte)(220 + (35 * t));
                    var g = (byte)(230 - (60 * t));
                    var b = (byte)(235 - (120 * t));
                    rect.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
                }

                var tooltip = new ToolTip { Content = $"{day:yyyy-MM-dd}: {count:N0} tokens" };
                rect.ToolTip = tooltip;

                var localDay = day; // capture
                rect.MouseLeftButtonUp += (_, _) =>
                {
                    _filterStartDate = localDay;
                    _filterEndDate = localDay;
                    var filtered = GetFilteredGroupsByDate(_allGroups, _filterStartDate, _filterEndDate);
                    PopulateSessionTree(filtered);
                    _sessionsSubtitleText.Text = $"{filtered.Sum(g=>g.Sessions.Count):N0} sessions on {localDay:yyyy-MM-dd}";
                };

                _tokenHeatBar.Children.Add(rect);
            }
        }
        else
        {
            // bucket into groups
            var bucketSize = (int)Math.Ceiling(totalDays / (double)maxRects);
            var buckets = (int)Math.Ceiling(totalDays / (double)bucketSize);
            for (var bIndex = 0; bIndex < buckets; bIndex++)
            {
                var startDay = min.AddDays(bIndex * bucketSize);
                var endDay = min.AddDays(Math.Min((bIndex + 1) * bucketSize - 1, totalDays - 1));
                long sumTokens = 0;
                for (var d = 0; d < bucketSize; d++)
                {
                    var day = startDay.AddDays(d);
                    sumTokens += _tokensByDate.GetValueOrDefault(day);
                }

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = rectWidth * bucketSize,
                    Height = _tokenHeatBar.Height,
                    Margin = new Thickness(0.5, 0, 0.5, 0),
                    Fill = new SolidColorBrush(Color.FromRgb(245, 245, 247))
                };

                if (sumTokens > 0 && maxTokens > 0)
                {
                    var t = (double)sumTokens / (double)maxTokens;
                    var r = (byte)(220 + (35 * t));
                    var g = (byte)(230 - (60 * t));
                    var b = (byte)(235 - (120 * t));
                    rect.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
                }

                var tooltip = new ToolTip { Content = $"{startDay:yyyy-MM-dd}..{endDay:yyyy-MM-dd}: {sumTokens:N0} tokens" };
                rect.ToolTip = tooltip;

                var localStart = startDay;
                var localEnd = endDay;
                rect.MouseLeftButtonUp += (_, _) =>
                {
                    _filterStartDate = localStart;
                    _filterEndDate = localEnd;
                    var filtered = GetFilteredGroupsByDate(_allGroups, _filterStartDate, _filterEndDate);
                    PopulateSessionTree(filtered);
                    _sessionsSubtitleText.Text = $"{filtered.Sum(g=>g.Sessions.Count):N0} sessions on {localStart:yyyy-MM-dd}..{localEnd:yyyy-MM-dd}";
                };

                _tokenHeatBar.Children.Add(rect);
            }
        }
    }

    private void SetSessionButtonsEnabled(bool isEnabled)
    {
        _toolbarResumeButton.IsEnabled = isEnabled;
        _toolbarCopyCommandButton.IsEnabled = isEnabled;
        _toolbarOpenProjectButton.IsEnabled = isEnabled;
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
    }

    private void InitializeCalendarPopup()
    {
        // Configure calendar control
        _calendarControl.SelectionMode = System.Windows.Controls.CalendarSelectionMode.SingleRange;
        _calendarControl.DisplayDate = DateTime.Today;
        _calendarControl.SelectedDatesChanged += CalendarControl_SelectedDatesChanged;
        _calendarControl.DisplayDateChanged += (_, _) => UpdateCalendarHeatmapIfVisible();

        // Build popup content: calendar + action buttons
        var panel = new StackPanel { Background = Brushes.White };
        panel.Children.Add(_calendarControl);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "Apply", Margin = new Thickness(6), Padding = new Thickness(8,4,8,4) };
        apply.Click += (_, _) =>
        {
            ApplyCalendarFilter();
            _calendarPopup.IsOpen = false;
        };
        var clear = new Button { Content = "Clear", Margin = new Thickness(6), Padding = new Thickness(8,4,8,4) };
        clear.Click += (_, _) =>
        {
            ClearCalendarFilter();
            _calendarPopup.IsOpen = false;
        };
        buttons.Children.Add(clear);
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);

        var border = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(216, 222, 233)), BorderThickness = new Thickness(1), Child = panel };

        _calendarPopup.Child = border;
        _calendarPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _calendarPopup.StaysOpen = false;
    }

    private void ToggleCalendarPopup(UIElement placementTarget)
    {
        if (_calendarPopup.IsOpen)
        {
            _calendarPopup.IsOpen = false;
            return;
        }

        _calendarPopup.PlacementTarget = placementTarget;
        UpdateSessionsByDateCache();
        UpdateCalendarHeatmapIfVisible();
        _calendarPopup.IsOpen = true;
    }

    private void ApplyCalendarFilter()
    {
        // Some calendar interactions may leave SelectedDates empty but set SelectedDate.
        if (_calendarControl.SelectedDates.Count == 0 && _calendarControl.SelectedDate.HasValue)
        {
            var d = _calendarControl.SelectedDate.Value.Date;
            _filterStartDate = DateOnly.FromDateTime(d);
            _filterEndDate = _filterStartDate;
        }
        else if (_calendarControl.SelectedDates.Count == 0)
        {
            _filterStartDate = null;
            _filterEndDate = null;
        }
        else
        {
            var dates = _calendarControl.SelectedDates.OrderBy(d => d).ToArray();
            _filterStartDate = DateOnly.FromDateTime(dates.First());
            _filterEndDate = DateOnly.FromDateTime(dates.Last());
        }

        // Re-populate tree with filtered groups
        var filtered = GetFilteredGroupsByDate(_allGroups, _filterStartDate, _filterEndDate);
        PopulateSessionTree(filtered);
        _sessionsSubtitleText.Text = _filterStartDate.HasValue && _filterEndDate.HasValue
            ? $"{filtered.Sum(g=>g.Sessions.Count):N0} sessions in {_filterStartDate.Value:yyyy-MM-dd}..{_filterEndDate.Value:yyyy-MM-dd}"
            : $"{_allGroups.Sum(g=>g.Sessions.Count):N0} total sessions";
    }

    private void ClearCalendarFilter()
    {
        _filterStartDate = null;
        _filterEndDate = null;
        PopulateSessionTree(_allGroups);
        _sessionsSubtitleText.Text = $"{_allGroups.Count:N0} projects · {_allGroups.Sum(g=>g.Sessions.Count):N0} sessions";
    }

    private void CalendarControl_SelectedDatesChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Apply immediately when selection changes so a single click is sufficient.
        try
        {
            ApplyCalendarFilter();
        }
        catch
        {
            // ignore errors from live apply
        }
    }

    private void UpdateSessionsByDateCache()
    {
        try
        {
            var dict = new Dictionary<DateOnly,int>();
            foreach (var session in _allGroups.SelectMany(g => g.Sessions))
            {
                if (session.LastUpdated.HasValue)
                {
                    var d = DateOnly.FromDateTime(session.LastUpdated.Value.LocalDateTime.Date);
                    dict[d] = dict.GetValueOrDefault(d) + 1;
                }
            }
            _sessionsByDate = dict;
        }
        catch
        {
            _sessionsByDate = new Dictionary<DateOnly,int>();
        }
    }

    private void UpdateCalendarHeatmapIfVisible()
    {
        if (!_calendarPopup.IsOpen) return;
        UpdateCalendarHeatmap(_calendarControl);
    }

    private void UpdateCalendarHeatmap(System.Windows.Controls.Calendar calendar)
    {
        if (calendar is null) return;
        // find CalendarDayButton children and color them
        var max = _sessionsByDate.Count == 0 ? 0 : _sessionsByDate.Values.Max();
        foreach (var button in FindVisualChildren<System.Windows.Controls.Primitives.CalendarDayButton>(calendar))
        {
            if (button.DataContext is DateTime dt)
            {
                var day = DateOnly.FromDateTime(dt.Date);
                var count = _sessionsByDate.GetValueOrDefault(day);
                if (count == 0)
                {
                    button.ClearValue(Control.BackgroundProperty);
                    button.ToolTip = null;
                }
                else
                {
                    // map count to red intensity (light->deep)
                    var t = max == 0 ? 0.0 : (double)count / max;
                    // compute color between light pink and deep red
                    var r = (byte)(220 + (35 * t));
                    var g = (byte)(230 - (60 * t));
                    var b = (byte)(235 - (120 * t));
                    button.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                    button.ToolTip = $"{count} sessions on {day:yyyy-MM-dd}";
                }
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }


    private void SetPromptContent(string text)
    {
        SetRichTextBoxText(_promptText, text ?? string.Empty, _activeSearchQuery);
    }

    private static string BuildProjectMetadata(ProjectGroup project)
    {
        return string.Join(Environment.NewLine,
            $"Project: {project.DisplayName}",
            $"Project key: {project.ProjectKey}",
            $"Project root: {project.ProjectRoot}",
            $"Sessions: {project.Sessions.Count:N0}",
            $"Latest: {FormatDate(project.LastUpdated)}",
            $"Total size: {FormatBytes(project.Sessions.Sum(session => session.FileSizeBytes))}");
    }

    private static string BuildSessionMetadata(SessionSummary session, int? promptCount)
    {
        return string.Join(Environment.NewLine,
            $"Title: {session.Title ?? "(first prompt not found)"}",
            $"Session ID: {session.SessionId}",
            $"Project key: {session.ProjectKey}",
            $"Project root: {session.ProjectRoot}",
            $"Project hash: {session.ProjectHash ?? "(missing)"}",
            $"Kind: {session.Kind ?? "(missing)"}",
            $"Start time: {FormatDate(session.StartTime)}",
            $"Last updated: {FormatDate(session.LastUpdated)}",
            $"Prompt count: {(promptCount.HasValue ? promptCount.Value.ToString("N0") : "not loaded")}",
            $"File size: {FormatBytes(session.FileSizeBytes)}",
            $"Session file: {session.FilePath}",
            string.Empty,
            $"Resume command: gpc --skip-trust --approval-mode yolo --resume {session.SessionId}");
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "(missing)";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:N1} {units[unitIndex]}";
    }

    private async Task<string> GetWorkspaceStatusAsync(string projectRoot, string? sessionProjectHash)
    {
        // Run lightweight git commands to determine current HEAD and count of uncommitted files.
        try
        {
            if (!Directory.Exists(projectRoot))
            {
                return "Workspace: directory not found";
            }

            // First, check if this is a git repository (has .git)
            var gitDir = Path.Combine(projectRoot, ".git");
            if (!Directory.Exists(gitDir))
            {
                // Fallback: try running `git rev-parse --git-dir` to detect submodules/worktrees
                var check = await RunProcessAsync("git", "rev-parse --git-dir", projectRoot);
                if (string.IsNullOrWhiteSpace(check.Output) || check.ExitCode != 0)
                {
                    return "Workspace: not a git repository";
                }
            }

            var head = await RunProcessAsync("git", "rev-parse --short HEAD", projectRoot);
            var headHash = head.ExitCode == 0 ? head.Output.Trim() : null;

            var status = await RunProcessAsync("git", "status --porcelain=v1", projectRoot);
            var statusLines = !string.IsNullOrEmpty(status.Output)
                ? status.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();

            var uncommittedCount = statusLines.Length;
            var untracked = statusLines.Count(l => l.StartsWith("?? "));

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(headHash))
            {
                parts.Add($"HEAD: {headHash}");
                if (!string.IsNullOrWhiteSpace(sessionProjectHash) && !string.Equals(sessionProjectHash, headHash, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add($"(session hash: {sessionProjectHash})");
                }
            }

            if (uncommittedCount == 0)
            {
                parts.Add("Working tree: clean");
            }
            else
            {
                parts.Add($"Working tree: {uncommittedCount} changed{(untracked > 0 ? $", {untracked} untracked" : string.Empty)}");
            }

            return "Repository status: " + string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return "Workspace status unavailable: " + ex.Message;
        }
    }

    private static Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        return Task.Run(() =>
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo(fileName)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                using var process = System.Diagnostics.Process.Start(startInfo)!;
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, output, error);
            }
            catch (Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }
        });
    }
}
