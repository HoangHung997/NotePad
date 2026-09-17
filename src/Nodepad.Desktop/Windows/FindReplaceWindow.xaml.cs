using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WpfRichTextBox = System.Windows.Controls.RichTextBox;

namespace Nodepad.Desktop.Windows;

public partial class FindReplaceWindow : Window
{
    private readonly WpfRichTextBox _editor;

    public FindReplaceWindow(WpfRichTextBox editor)
    {
        InitializeComponent();
        _editor = editor;
        Loaded += (_, _) =>
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
        };
    }

    private StringComparison Comparison =>
        MatchCaseCheckBox.IsChecked == true
            ? StringComparison.CurrentCulture
            : StringComparison.CurrentCultureIgnoreCase;

    private void FindNextButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectNextMatch(true);
    }

    private void ReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FindTextBox.Text))
        {
            FindTextBox.Focus();
            return;
        }

        if (_editor.Selection.IsEmpty || !string.Equals(_editor.Selection.Text, FindTextBox.Text, Comparison))
        {
            if (!SelectNextMatch(true))
            {
                return;
            }
        }

        _editor.Selection.Text = ReplaceTextBox.Text;
        SelectNextMatch(true);
    }

    private void ReplaceAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FindTextBox.Text))
        {
            FindTextBox.Focus();
            return;
        }

        var replacements = 0;
        var start = _editor.Document.ContentStart;
        while (TryFindRange(start, out var range))
        {
            range.Text = ReplaceTextBox.Text;
            replacements++;
            start = range.Start.GetPositionAtOffset(ReplaceTextBox.Text.Length, LogicalDirection.Forward) ?? range.End;
        }

        System.Windows.MessageBox.Show(
            replacements == 0 ? "No matches found." : $"Replaced {replacements} occurrence(s).",
            "Find & Replace",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private bool SelectNextMatch(bool wrapAround)
    {
        if (string.IsNullOrWhiteSpace(FindTextBox.Text))
        {
            FindTextBox.Focus();
            return false;
        }

        var start = _editor.Selection.IsEmpty
            ? _editor.CaretPosition
            : _editor.Selection.End;

        if (!TryFindRange(start, out var range) && wrapAround)
        {
            TryFindRange(_editor.Document.ContentStart, out range);
        }

        if (range is null)
        {
            System.Windows.MessageBox.Show(
                "No more matches found.",
                "Find & Replace",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        _editor.Selection.Select(range.Start, range.End);
        _editor.Focus();
        return true;
    }

    private bool TryFindRange(TextPointer start, out TextRange range)
    {
        range = null!;
        if (string.IsNullOrWhiteSpace(FindTextBox.Text))
        {
            return false;
        }

        var navigator = start;
        while (navigator is not null)
        {
            if (navigator.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var runText = navigator.GetTextInRun(LogicalDirection.Forward);
                var matchIndex = runText.IndexOf(FindTextBox.Text, Comparison);
                if (matchIndex >= 0)
                {
                    var rangeStart = navigator.GetPositionAtOffset(matchIndex, LogicalDirection.Forward);
                    var rangeEnd = rangeStart?.GetPositionAtOffset(FindTextBox.Text.Length, LogicalDirection.Forward);
                    if (rangeStart is not null && rangeEnd is not null)
                    {
                        range = new TextRange(rangeStart, rangeEnd);
                        return true;
                    }
                }
            }

            navigator = navigator.GetNextContextPosition(LogicalDirection.Forward);
        }

        return false;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
