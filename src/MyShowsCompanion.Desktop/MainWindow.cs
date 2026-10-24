using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MyShowsCompanion.Domain;
using MyShowsCompanion.Import;
using MyShowsCompanion.Services;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Desktop;

public sealed class MainWindow : Window
{
    private readonly SqliteStore _store;
    private readonly LibraryService _library;
    private readonly WorkbookImporter _importer;
    private readonly Button _syncButton;
    private readonly Button _exportButton;
    private readonly TextBlock _lastImport;
    private readonly TextBlock _seriesCount;
    private readonly TextBlock _movieCount;
    private readonly TextBlock _episodeCount;
    private readonly TextBlock _unresolvedBanner;
    private readonly Button _unresolvedButton;
    private readonly TextBlock _status;
    private readonly CatalogPane<Series> _seriesPane;
    private readonly CatalogPane<Movie> _moviePane;
    private LibrarySnapshot? _snapshot;

    public MainWindow()
    {
        Title = "MyShows Companion";
        Width = 1240; Height = 820; MinWidth = 900; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var databasePath = ResolveDatabasePath();
        _store = new SqliteStore(databasePath);
        _library = new LibraryService(_store);
        _importer = new WorkbookImporter(_store);

        _syncButton = new Button { Content = "Синхронизировать", Padding = new Thickness(18, 10), MinWidth = 174 };
        _syncButton.Click += async (_, _) => await SynchronizeAsync();
        _exportButton = new Button { Content = "Экспорт для ChatGPT", Padding = new Thickness(18, 10), MinWidth = 190 };
        _exportButton.Click += async (_, _) => await ExportForChatGptAsync();
        _lastImport = new TextBlock { Text = "Последний импорт: нет данных", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush.Parse("#657180") };
        _seriesCount = new TextBlock(); _movieCount = new TextBlock(); _episodeCount = new TextBlock();
        _unresolvedBanner = new TextBlock { IsVisible = false, Foreground = Brush.Parse("#657180"), VerticalAlignment = VerticalAlignment.Center };
        _unresolvedButton = new Button { Content = "Показать", IsVisible = false };
        _unresolvedButton.Click += async (_, _) => await ShowUnresolvedAsync();
        _status = new TextBlock { Text = "Загрузка локальной библиотеки…", Foreground = Brush.Parse("#657180"), Margin = new Thickness(16, 8) };

        _seriesPane = CreateSeriesPane();
        _moviePane = CreateMoviePane();

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        var top = new StackPanel { Spacing = 16 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "MyShows Companion", FontSize = 26, FontWeight = FontWeight.SemiBold },
                _lastImport
            }
        });
        Grid.SetColumn(_exportButton, 1); header.Children.Add(_exportButton);
        Grid.SetColumn(_syncButton, 2); header.Children.Add(_syncButton);
        top.Children.Add(header);

        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        cards.Children.Add(StatCard("Сериалы", _seriesCount));
        cards.Children.Add(StatCard("Фильмы", _movieCount));
        cards.Children.Add(StatCard("Просмотрено эпизодов", _episodeCount));
        top.Children.Add(cards);

        var warningsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
        warningsRow.Name = "WarningsRow";
        warningsRow.Children.Add(_unresolvedBanner);
        Grid.SetColumn(_unresolvedButton, 1); warningsRow.Children.Add(_unresolvedButton);
        top.Children.Add(warningsRow);

        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var tabs = new TabControl
        {
            Margin = new Thickness(0, 18, 0, 0),
            ItemsSource = new[]
            {
                new TabItem { Header = "Сериалы", Content = _seriesPane.Root },
                new TabItem { Header = "Фильмы", Content = _moviePane.Root }
            }
        };
        root.Children.Add(tabs);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        Content = root;
        Opened += async (_, _) => await RefreshAsync();
    }

    private static Border StatCard(string caption, TextBlock value)
    {
        value.Foreground = Brush.Parse("#1D242C");
        value.FontSize = 20;
        value.FontWeight = FontWeight.SemiBold;
        return new Border
        {
            Background = Brush.Parse("#F3F6FA"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12),
            MinWidth = 190,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = caption, FontSize = 12, Foreground = Brush.Parse("#657180") },
                    value
                }
            }
        };
    }

    private CatalogPane<Series> CreateSeriesPane()
    {
        var pane = new CatalogPane<Series>(
            new[] { "Сериал", "Статус", "Оценка", "Просмотрено эпизодов", "Осталось эпизодов" },
            new[] { nameof(Series.Title), nameof(Series.Status), nameof(Series.Rating), nameof(Series.WatchedEpisodeCount), nameof(Series.RemainingEpisodeCount) },
            new[] { "Смотрю", "Полностью посмотрел", "Перестал", "Буду смотреть" });
        pane.Apply = (search, status, rated, sort) => _snapshot is null ? Array.Empty<Series>() :
            LibraryQueries.FilterSeries(_snapshot.Series, search, status, rated, sort);
        return pane;
    }

    private CatalogPane<Movie> CreateMoviePane()
    {
        var pane = new CatalogPane<Movie>(
            new[] { "Фильм", "Статус", "Оценка", "Дата просмотра", "Длительность, мин." },
            new[] { nameof(Movie.Title), nameof(Movie.Status), nameof(Movie.Rating), nameof(Movie.WatchedDate), nameof(Movie.DurationMinutes) },
            new[] { "Полностью посмотрел", "Буду смотреть" });
        pane.Apply = (search, status, rated, sort) => _snapshot is null ? Array.Empty<Movie>() :
            LibraryQueries.FilterMovies(_snapshot.Movies, search, status, rated, sort);
        return pane;
    }

    private async Task SynchronizeAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите экспорт профиля MyShows",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Книга Excel") { Patterns = new[] { "*.xlsx" } } }
            });
            if (files.Count == 0) return;
            var path = files[0].Path.LocalPath;
            _syncButton.IsEnabled = false;
            _status.Text = "Импортирую XLSX… Можно продолжать работать с окном.";
            var result = await Task.Run(() => _importer.Import(path));
            await RefreshAsync(showErrors: false);
            var duplicate = result.Run.ErrorsAndWarnings.Contains("SHA-256", StringComparison.OrdinalIgnoreCase);
            var message = duplicate
                ? "Этот файл уже был импортирован. Изменений нет."
                : $"Импорт завершён.\n\nДобавлено: {result.Run.ImportedCount}\nОбновлено: {result.Run.UpdatedCount}\nПропущено: {result.Run.SkippedCount}\nUnresolved: {_snapshot?.UnresolvedCount ?? result.UnresolvedCount}";
            var warningLines = _snapshot?.Unresolved.Select(x => $"• {x.Message} ({x.Count})") ?? Enumerable.Empty<string>();
            var runWarnings = result.Run.ErrorsAndWarnings.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !x.Contains("SHA-256", StringComparison.OrdinalIgnoreCase));
            var warnings = warningLines.Concat(runWarnings).Distinct().Take(18).ToArray();
            if (warnings.Length > 0) message += "\n\nПредупреждения:\n" + string.Join("\n", warnings);
            await ShowMessageAsync("Синхронизация", message);
            _status.Text = duplicate ? "Файл уже импортирован; локальная библиотека не изменилась." : "Библиотека обновлена из XLSX.";
        }
        catch (Exception ex)
        {
            _status.Text = "Синхронизация не выполнена.";
            await ShowMessageAsync("Не удалось импортировать файл", FriendlyError(ex));
        }
        finally { _syncButton.IsEnabled = true; }
    }

    private async Task ExportForChatGptAsync()
    {
        try
        {
            _exportButton.IsEnabled = false;
            _status.Text = "Подготавливаю компактный экспорт из локальной базы…";
            var export = await Task.Run(() => ChatGptLibraryExporter.Read(_store));
            var bytes = await Task.Run(() => ChatGptLibraryExporter.Serialize(export));
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Экспорт библиотеки для ChatGPT",
                SuggestedFileName = "myshows-chatgpt.json",
                DefaultExtension = "json"
            });
            if (file is null)
            {
                _status.Text = "Экспорт отменён.";
                return;
            }

            await using (var stream = await file.OpenWriteAsync())
                await stream.WriteAsync(bytes);

            var path = file.Path.IsFile ? file.Path.LocalPath : file.Name;
            await ShowMessageAsync("Экспорт готов",
                $"Сериалов экспортировано: {export.Statistics.TotalSeries:N0}\n" +
                $"Фильмов экспортировано: {export.Statistics.TotalMovies:N0}\n\n" +
                $"Файл сохранён:\n{path}");
            _status.Text = "Экспорт библиотеки для ChatGPT сохранён.";
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось экспортировать библиотеку.";
            await ShowMessageAsync("Ошибка экспорта", FriendlyError(ex));
        }
        finally { _exportButton.IsEnabled = true; }
    }

    private async Task RefreshAsync(bool showErrors = true)
    {
        try
        {
            _snapshot = await Task.Run(_library.Load);
            _seriesCount.Text = _snapshot.Series.Count.ToString("N0");
            _movieCount.Text = _snapshot.Movies.Count.ToString("N0");
            _episodeCount.Text = _snapshot.WatchedEpisodes.ToString("N0");
            _lastImport.Text = _snapshot.LastImport is null ? "Последний импорт: нет данных" :
                $"Последний успешный импорт: {_snapshot.LastImport.Value.ToLocalTime():dd.MM.yyyy HH:mm}";
            _seriesPane.Refresh(); _moviePane.Refresh();
            _unresolvedBanner.Text = _snapshot.UnresolvedCount == 0 ? "" : $"Неоднозначности импорта: {_snapshot.UnresolvedCount:N0}";
            _unresolvedBanner.IsVisible = _snapshot.UnresolvedCount > 0;
            _unresolvedButton.IsVisible = _snapshot.Unresolved.Count > 0;
            if (_unresolvedBanner.Parent is Grid row) row.IsVisible = _snapshot.Unresolved.Count > 0;
            _status.Text = "Локальная база загружена.";
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось загрузить локальную библиотеку.";
            if (showErrors) await ShowMessageAsync("Ошибка базы данных", FriendlyError(ex));
        }
    }

    private async Task ShowUnresolvedAsync()
    {
        var list = new StackPanel { Spacing = 12 };
        foreach (var issue in _snapshot?.Unresolved ?? Array.Empty<(string Message, int Count)>())
            list.Children.Add(new TextBlock { Text = $"{issue.Message} ({issue.Count})", TextWrapping = TextWrapping.Wrap });
        var snapshot = _snapshot;
        var summary = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        if (snapshot is not null)
        {
            if (snapshot.UnlinkedEpisodeCount > 0) summary.Children.Add(new TextBlock { Text = $"Эпизоды без однозначной связи с сериалом: {snapshot.UnlinkedEpisodeCount:N0}" });
            if (snapshot.AmbiguousSeriesCount > 0) summary.Children.Add(new TextBlock { Text = $"Неоднозначные записи сериалов: {snapshot.AmbiguousSeriesCount:N0}" });
            if (snapshot.AmbiguousMovieCount > 0) summary.Children.Add(new TextBlock { Text = $"Неоднозначные записи фильмов: {snapshot.AmbiguousMovieCount:N0}" });
            summary.Children.Add(new TextBlock { Text = "Все записи сохранены. Неоднозначности не влияют на экспорт библиотеки для ChatGPT.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#657180") });
        }
        var dialog = new Window
        {
            Title = "Неоднозначные записи", Width = 680, Height = 440, MinWidth = 420, MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = $"Всего неоднозначностей: {_snapshot?.UnresolvedCount ?? 0}" },
                            summary
                        },
                        [DockPanel.DockProperty] = Dock.Top
                    },
                    new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
                }
            }
        };
        await dialog.ShowDialog(this);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var close = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100 };
        var dialogContent = new DockPanel { Margin = new Thickness(22) };
        var messageScroll = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22 }, Margin = new Thickness(0,0,0,18) };
        DockPanel.SetDock(close, Dock.Bottom);
        dialogContent.Children.Add(close);
        dialogContent.Children.Add(messageScroll);
        var dialog = new Window
        {
            Title = title, Width = 620, Height = 400, MinWidth = 420, MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = dialogContent
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        InvalidDataException => $"Формат книги не распознан. Проверьте листы и названия колонок.\n\n{ex.Message}",
        UnauthorizedAccessException => "Нет доступа к выбранному файлу или локальной базе данных.",
        IOException => $"Не удалось прочитать XLSX. Закройте файл в другой программе и повторите попытку.\n\n{ex.Message}",
        _ => $"Операция не выполнена. Старые данные сохранены.\n\n{ex.Message}"
    };

    private static string ResolveDatabasePath()
    {
        var supportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyShowsCompanion");
        return LocalDatabaseBootstrapper.EnsureDatabase(supportDirectory, FindLegacyDatabasePath());
    }

    private static string? FindLegacyDatabasePath()
    {
        // When the bundle is kept beside the old project database, walk up from Contents/MacOS.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "MyShowsCompanion.sqlite3");
            if (File.Exists(candidate)) return candidate;
        }

        // Also support an app copied elsewhere after its first build on this Mac.
        var projectCandidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projects", "MyShowsCompanion", "MyShowsCompanion.sqlite3");
        return File.Exists(projectCandidate) ? projectCandidate : null;
    }

    private sealed class CatalogPane<T>
    {
        private readonly TextBox _search;
        private readonly ComboBox _status;
        private readonly ComboBox _sort;
        private readonly CheckBox _rated;
        private readonly DataGrid _grid;
        public Control Root { get; }
        public Func<string?, string?, bool, CatalogSort, IReadOnlyList<T>> Apply { get; set; } = (_, _, _, _) => Array.Empty<T>();

        public CatalogPane(string[] headers, string[] properties, string[] statuses)
        {
            _search = new TextBox { PlaceholderText = "Поиск по названию", MinWidth = 250 };
            _status = new ComboBox { ItemsSource = new[] { "Все статусы" }.Concat(statuses).Distinct().ToArray(), SelectedIndex = 0, MinWidth = 210 };
            _rated = new CheckBox { Content = "Только оценённые", VerticalAlignment = VerticalAlignment.Center };
            _sort = new ComboBox { ItemsSource = new[] { "По названию", "По оценке" }, SelectedIndex = 0, MinWidth = 150 };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 12), Children = { _search, _status, _rated, _sort } };
            _grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserSortColumns = false, RowHeight = 36, ColumnHeaderHeight = 40 };
            for(var i=0;i<headers.Length;i++) _grid.Columns.Add(new DataGridTextColumn { Header=headers[i], Binding=new Binding(properties[i]), Width=i==0?new DataGridLength(2,DataGridLengthUnitType.Star):new DataGridLength(1,DataGridLengthUnitType.Star) });
            Root = new DockPanel { LastChildFill = true, Children = { controls, _grid } };
            DockPanel.SetDock(controls, Dock.Top);
            _search.TextChanged += (_, _) => Refresh(); _status.SelectionChanged += (_, _) => Refresh();
            _rated.IsCheckedChanged += (_, _) => Refresh(); _sort.SelectionChanged += (_, _) => Refresh();
        }

        public void Refresh()
        {
            _grid.ItemsSource = Apply(_search.Text, _status.SelectedItem as string, _rated.IsChecked == true,
                _sort.SelectedIndex == 1 ? CatalogSort.Rating : CatalogSort.Title);
        }
    }
}
