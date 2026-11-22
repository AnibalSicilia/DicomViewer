using System.Text.Json;
using System.Windows;
using System.Windows.Controls; // SelectionChanged
using System.Windows.Input; // mouse events

namespace DicomViewer
{
    /// <summary>
    /// Interaction logic for StudyBrowserWindow.xaml
    /// </summary>
    public partial class StudyBrowserWindow : Window
    {
        // One row in the study table
        public record StudyRow(string StudyUid, string Patient, string PatientId, string Date,
            string Modalities, string Description, string SeriesCount);
        // One row in the series table
        public record SeriesRow(string SeriesUid, string Number, string Modality, 
            string Description, string ImageCount);
        // The servers we are browsing
        private readonly PacsServer _server;
        // What the user chose (MainWindow reads these after the windows closes)
        public string SelectedStudyUid { get; private set; } = "";
        public string SelectedSeriesUid { get; private set; } = "";
        public StudyBrowserWindow(PacsServer server)
        {
            InitializeComponent();
            _server = server;
            Title = $"Studies on {server.Name}";

            // Start loading as soon as the window is on screen
            Loaded += async (sender, e) => await LoadStudiesAsync();
        }

        // QIDO-RS: ask the server for its studies and fill the table
        private async Task LoadStudiesAsync()
        {
            try
            {
                // Ask for 50 studies, plus three extra fields some servers leave out by default:
                // Study Description (0008,1030), Modalities in Study (0008,0061), Number of Series (0020,1206)
                string url = $"{_server.DicomWebRoot}/studies?limit=50" +
                             "&includefield=00081030&includefield=00080061&includefield=00201206";
                string json = await MainWindow.Http.GetStringAsync(url);

                var rows = new List<StudyRow>();

                // Some servers send an empty answer when there are no results
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    foreach (JsonElement item in doc.RootElement.EnumerateArray()) 
                    {
                        rows.Add(new StudyRow(
                            StudyUid: DicomJson.GetText(item, "0020000D"),
                            Patient: DicomJson.GetText(item, "00100010"),
                            PatientId: DicomJson.GetText(item, "00100020"),
                            Date: MainWindow.FormatDate(DicomJson.GetText(item, "00080020")),
                            Modalities: DicomJson.GetText(item, "00080061"),
                            Description: DicomJson.GetText(item, "00081030"),
                            SeriesCount: DicomJson.GetText(item, "00201206")));
                    }
                }

                StudyList.ItemsSource = rows;
                StatusText.Text = $"{rows.Count} studies on {_server.Name}. Click one to see its series.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load the studies: {ex.Message}";
            }
        }

        // When the user clicks a study, load its series
        private async void StudyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SeriesList.ItemsSource = null;  // clear the old series
            OpenSeriesButton.IsEnabled = false; // no series chosen yet

            if (StudyList.SelectedItem is not StudyRow study)
                return; // nothing selected

            await LoadSeriesAsync(study);
        }

        // QIDO-RS: ask the server for the series of one study
        private async Task LoadSeriesAsync(StudyRow study)
        {
            StatusText.Text = $"Loading the series of {study.Patient}...";

            try
            {
                // Also ask for Series Description (0008,103E) and Number of Images (0020,1209)
                string url = $"{_server.DicomWebRoot}/studies/{study.StudyUid}/series" +
                    "?includefield=0008103E&includefield=00201209";
                string json = await MainWindow.Http.GetStringAsync(url);

                // If the user clicked another study while we were waiting, forget this answer
                if (!ReferenceEquals(StudyList.SelectedItem, study))
                    return;

                var rows = new List<SeriesRow>();
                if(!string.IsNullOrWhiteSpace(json))
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        rows.Add(new SeriesRow(
                            SeriesUid: DicomJson.GetText(item, "0020000E"),
                            Number: DicomJson.GetText(item, "00200011"),
                            Modality: DicomJson.GetText(item, "00080060"),
                            Description: DicomJson.GetText(item, "0008103E"),
                            ImageCount: DicomJson.GetText(item, "00201209")));
                    }
                }

                // Sort by series number (1, 2, 3 ... not "1", "10", "2")
                rows = rows.OrderBy(r => int.TryParse(r.Number, out int n) ? n : int.MaxValue).ToList();

                SeriesList.ItemsSource = rows;
                StatusText.Text = $"{rows.Count} series in this study.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load the series: {ex.Message}";
            }
        }

        // A series is selected: the Open button can be used
        private void SeriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OpenSeriesButton.IsEnabled = SeriesList.SelectedItem != null;
        }

        // Double-click on a series: open it straight away
        private void SeriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SeriesList.SelectedItem != null)
                OpenSelectedSeries();
        }

        private void OpenSeriesButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedSeries();
        }

        private void OpenSelectedSeries()
        {
            var study = (StudyRow)StudyList.SelectedItem;
            var series = (SeriesRow)SeriesList.SelectedItem;

            SelectedStudyUid = study.StudyUid;
            SelectedSeriesUid = series.SeriesUid;

            DialogResult = true; // closes the window; ShowDialog() returns true;
        }
    }
}
