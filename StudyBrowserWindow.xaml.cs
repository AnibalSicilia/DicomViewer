using System.Text.Json;
using System.Windows;


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
        // The servers we are browsing
        private readonly PacsServer _server;
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
                StatusText.Text = $"{rows.Count} studies on {_server.Name}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load the studies: {ex.Message}";
            }
        }
    }
}
