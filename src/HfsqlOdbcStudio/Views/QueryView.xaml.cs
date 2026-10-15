using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.ViewModels;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Wpf.Ui.Appearance;

namespace HfsqlOdbcStudio.Views;

public partial class QueryView : UserControl
{
    private static IHighlightingDefinition? _sqlHighlighting;
    private MainViewModel? _vm;

    public QueryView()
    {
        InitializeComponent();
        SqlEditor.SyntaxHighlighting = LoadHighlighting();
        SqlEditor.Options.ConvertTabsToSpaces = true;
        SqlEditor.Options.IndentationSize = 4;
        SqlEditor.Options.EnableHyperlinks = false;
        SqlEditor.Options.HighlightCurrentLine = true;

        // Ctrl+Enter must execute instead of inserting a line break inside the editor.
        SqlEditor.TextArea.PreviewKeyDown += OnEditorPreviewKeyDown;

        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyEditorTheme();
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.BeginInvoke(ApplyEditorTheme);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.Query.Editor = new AvalonEditAdapter(SqlEditor);
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrlEnter = e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control;
        if (!ctrlEnter && e.Key != Key.F5) return;
        e.Handled = true;
        var command = _vm?.Query.ExecuteCommand;
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || HistoryList.SelectedItem is not Models.HistoryEntry entry) return;
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(HistoryList, d) is null) return;
        _vm.Query.LoadHistoryCommand.Execute(entry);
    }

    /// <summary>Shows the real column name (stored as caption) instead of the safe binding name c0..cN.</summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (ResultsGrid.ItemsSource is DataView view && e.PropertyDescriptor is PropertyDescriptor pd &&
            view.Table?.Columns[pd.Name] is { } column)
        {
            e.Column.Header = column.Caption;
            e.Column.SortMemberPath = pd.Name;
        }

        if (e.Column is DataGridTextColumn text)
        {
            text.MaxWidth = 480;
            text.ElementStyle = (Style)FindResource("ResultCellStyle");
        }
    }

    private void ApplyEditorTheme()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var h = SqlEditor.SyntaxHighlighting;
        if (h is not null)
        {
            SetColor(h, "Keyword", dark ? "#569CD6" : "#0000FF");
            SetColor(h, "Type", dark ? "#4EC9B0" : "#267F99");
            SetColor(h, "Function", dark ? "#DCDCAA" : "#795E26");
            SetColor(h, "String", dark ? "#CE9178" : "#A31515");
            SetColor(h, "Number", dark ? "#B5CEA8" : "#098658");
            SetColor(h, "Comment", dark ? "#6A9955" : "#008000");
            SetColor(h, "QuotedIdentifier", dark ? "#9CDCFE" : "#001080");
            SetColor(h, "Operator", dark ? "#D4D4D4" : "#383838");
        }

        var fg = (Brush)FindResource("TextFillColorPrimaryBrush");
        SqlEditor.TextArea.Caret.CaretBrush = fg;
        SqlEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x67, 0xC0));
        SqlEditor.TextArea.SelectionForeground = null;
        SqlEditor.TextArea.SelectionBorder = null;
        SqlEditor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        SqlEditor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        SqlEditor.TextArea.TextView.Redraw();
    }

    private static void SetColor(IHighlightingDefinition h, string name, string hex)
    {
        var color = h.GetNamedColor(name);
        if (color is null) return;
        color.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private static IHighlightingDefinition? LoadHighlighting()
    {
        if (_sqlHighlighting is not null) return _sqlHighlighting;
        try
        {
            using var stream = typeof(QueryView).Assembly.GetManifestResourceStream("HfsqlOdbcStudio.Assets.Sql.xshd");
            if (stream is null) return null;
            using var reader = XmlReader.Create(stream);
            _sqlHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            FileLog.Error("Loading SQL highlighting failed", ex);
        }

        return _sqlHighlighting;
    }

    private sealed class AvalonEditAdapter(TextEditor editor) : ISqlEditor
    {
        public string Text
        {
            get => editor.Text;
            set
            {
                // Replace (instead of setting Text) keeps the change undoable with Ctrl+Z.
                editor.Document.Replace(0, editor.Document.TextLength, value);
                editor.CaretOffset = editor.Document.TextLength;
                editor.ScrollToEnd();
            }
        }

        public string SelectedText => editor.SelectionLength > 0 ? editor.SelectedText : "";

        public void Focus() => editor.TextArea.Focus();
    }
}
