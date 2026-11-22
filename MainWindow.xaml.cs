using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Win32;

namespace DicomViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // The WADO-URI address of one brain MRI slice: study -> series -> image
        //private const string PacsUrl =
        //"https://orthanc.uclouvain.be/demo/wado?requestType=WADO" +
        //"&studyUID=2.16.840.1.113669.632.20.1211.10000357775" +
        //"&seriesUID=1.3.46.670589.11.0.0.11.4.2.0.8743.5.5396.2006120114285654497" +
        //"&objectUID=1.3.46.670589.11.0.0.11.4.2.0.8743.5.5396.2006120114313982544" +
        //"&contentType=application/dicom";
        // Where the series lives on the PACS: server -> study -> series
        private const string Server = "https://orthanc.uclouvain.be/demo";
        private const string StudyUid = "2.16.840.1.113669.632.20.1211.10000357775";
        private const string SeriesUid = "1.3.46.670589.11.0.0.11.4.2.0.8743.5.5396.2006120114285654497";

        // One HttpClient for the whole app (creating a new one per request is a known bad habit)
        private static readonly HttpClient Http = new HttpClient();

        // One slice of the series: its number, its DICOM data and its picture
        private record Slice(int Number, DicomDataset Dataset, BitmapSource Picture);

        // The whole series, in order. The grid, the filmstrip and < > all use this one list.
        private readonly List<Slice> _slices = new();

        // Which slice is shown large (position in _slices). -1 means none.
        private int _current = -1;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            // OpenFileDialog openFileDialog = new OpenFileDialog();
            // Pick a file
            var dialog = new OpenFileDialog
            {
                Title = "Choose a DICOM file",
                Filter = "DICOM files (*.dcm)|*.dcm|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                // Open file and read metadata
                //DicomFile file = DicomFile.Open(dialog.FileName);
                //ShowDicom(file);
                DicomDataset ds = DicomFile.Open(dialog.FileName).Dataset;
                ScanImage.Source = Render(ds);
                InfoText.Text = Describe(ds);

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open the file:\n{ex.Message}", "Error");
            }

        }

        private async void DownloadButton_Click(Object sender, RoutedEventArgs e)
        {
            DownloadButton.IsEnabled = false; // stop double clicks while downloading
            InfoText.Text = "Asking the PACS for the list of images...";

            try
            {
                // Ask the server for the file (same address you just hardcoded)
                //byte[] data = await Http.GetByteArrayAsync(PacsUrl);

                // Open it from memory instead of from disk
                //using var stream = new MemoryStream(data);
                //DicomFile file = DicomFile.Open(stream, FileReadOption.ReadAll);

                // call the method (same as open button)
                //ShowDicom(file);

                // 1. QIDO-RS: ask for the list of images in the series (same as in the browser)
                string listUrl = $"{Server}/dicom-web/studies/{StudyUid}/series/{SeriesUid}/instances";
                string json = await Http.GetStringAsync(listUrl);

                // 2. Read each image's SOP Instance UID (tag 0008, 0018) from the JSON
                var instanceUids = new List<string>();
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    foreach(JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        string uid = item.GetProperty("00080018").GetProperty("Value")[0].GetString()!;
                        instanceUids.Add(uid);
                    }
                }

                // 3. WADO-URI: download each image, one after the other
                _slices.Clear();
                for (int i = 0; i < instanceUids.Count; i++)
                {
                    InfoText.Text = $"Downloading image {i + 1} / {instanceUids.Count}...";

                    string imageUrl = $"{Server}/wado?requestType=WADO" +
                        $"&studyUID={StudyUid}&seriesUID={SeriesUid}" +
                        $"&objectUID={instanceUids[i]}&contentType=application/dicom";
                    byte[] data = await Http.GetByteArrayAsync(imageUrl);

                    using var stream = new MemoryStream(data);
                    DicomDataset ds = DicomFile.Open(stream, FileReadOption.ReadAll).Dataset;

                    int number = ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0);
                    _slices.Add(new Slice(number, ds, Render(ds)));
                }

                // 4. Sort by instance number, so the slices go in order through the head
                _slices.Sort((a, b) => a.Number.CompareTo(b.Number));

                // Temporary test: show the first slice (step 16 replaces this with the grid)
                ScanImage.Source = _slices[0].Picture;
                InfoText.Text = $"Downloaded {_slices.Count} slices. First is number {_slices[0].Number}, " +
                    $"last is number {_slices[^1].Number}.";
            }
            catch (Exception ex)
            {
                InfoText.Text = "Download failed";
                MessageBox.Show($"Could not download the series:\n{ex.Message}", "Error");
            }
            finally
            {
                DownloadButton.IsEnabled = true;
            }

        }

        // Turns the DICOM pixel data into a picture WPF can show
        private static BitmapSource Render(DicomDataset ds)
        {
            var dicomImage = new DicomImage(ds);
            using IImage rendered = dicomImage.RenderImage();
            byte[] pixels = rendered.AsBytes();

            return BitmapSource.Create(
                rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgra32, null, pixels, rendered.Width * 4);
        }

        private static string Describe(DicomDataset ds)
        {
            string patientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, "(none)");
            string modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "(none)");
            string studyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, "(none)");
            int rows = ds.GetSingleValueOrDefault(DicomTag.Rows, 0);
            int columns = ds.GetSingleValueOrDefault(DicomTag.Columns, 0);

            return $"Patient: {patientName} Modality: {modality} Study date: {studyDate}\n" +
                 $"Size: {columns} x {rows} pixels";
        }

        // Events that still need to be implemented
        private void BackToGridButton_Click(object sender, RoutedEventArgs e) {}
        private void PreviousButton_Click(object sender, RoutedEventArgs e) { }
        private void NextButton_Click(object sender, RoutedEventArgs e) { }
        private void SliceView_MouseWheel(object sender, MouseWheelEventArgs e) { }
        private void SliceView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }
        private void Window_KeyDown(object sender, KeyEventArgs e) { }
    }
}