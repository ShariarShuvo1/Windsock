using System.Windows.Controls;
using System.Windows.Input;

namespace Windsock.App.Commands;

/// <summary>
/// Commands a text field can carry in its own template.
/// </summary>
public static class TextCommands
{
    /// <summary>Empties the <see cref="TextBox"/> passed as the parameter.</summary>
    public static ICommand Clear { get; } = new ClearTextCommand();

    private sealed class ClearTextCommand : ICommand
    {
        /// <summary>Never raised: the command applies whenever it has a field.</summary>
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => parameter is TextBox;

        public void Execute(object? parameter)
        {
            if (parameter is TextBox box)
            {
                box.Clear();
                box.Focus();
            }
        }
    }
}
