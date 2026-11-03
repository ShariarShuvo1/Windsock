using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>Usage › Reports: a period's report, two compared, a year, the week by the hour, and all time.</summary>
public partial class ReportsPanelView : UserControl
{
    private readonly ReportsPanelViewModel _viewModel;

    public ReportsPanelView(ReportsPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // Visibility rather than Loaded, which a minimised window never loses.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                viewModel.Show();
                return;
            }

            viewModel.Hide();
        };

        Unloaded += (_, _) => viewModel.Hide();
        SaveCardButton.Click += OnSaveCard;
    }

    /// <summary>Draws the month on show to a picture and saves it where the reader chooses.</summary>
    private void OnSaveCard(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Card() is not { } model)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "Save the month as a picture",
            FileName = _viewModel.CardFileName(),
            DefaultExt = ".png",
            Filter = "PNG picture (*.png)|*.png",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            MonthCard card = new(model);
            MonthCard.Save(card.Render(), dialog.FileName);
            _viewModel.Tell("Saved " + Path.GetFileName(dialog.FileName) + ".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.Tell("The picture could not be saved there: " + ex.Message);
        }
    }
}
