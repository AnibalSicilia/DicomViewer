using System.IO;
using System.Net.Http;
using System.Text;
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
        private const string PacsUrl =
            "https://orthanc.uclouvain.be/demo/wado?requestType=WADO" +
            "&studyUID=2.16.840.1.113669.632.20.1211.10000357775" +
            "&seriesUID=1.3.46.670589.11.0.0.11.4.2.0.8743.5.5396.2006120114285654497" +
            "&objectUID=1.3.46.670589.11.0.0.11.4.2.0.8743.5.5396.2006120114313982544" +
            "&contentType=application/dicom";

        // One HttpClient for the whole app (creating a new one per request is a known bad habit)
        private static readonly HttpClient Http = new HttpClient();

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
                DicomFile file = DicomFile.Open(dialog.FileName);
                ShowDicom(file);

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open the file:\n{ex.Message}", "Error");
            }

        }

        private async void DownloadButton_Click(Object sender, RoutedEventArgs e)
        {
            DownloadButton.IsEnabled = false; // stop double clicks while downloading
            InfoText.Text = "Downloading from PACS...";

            try
            {
                // Ask the server for the file (same address you just hardcoded)
                byte[] data = await Http.GetByteArrayAsync(PacsUrl);

                // Open it from memory instead of from disk
                using var stream = new MemoryStream(data);
                DicomFile file = DicomFile.Open(stream, FileReadOption.ReadAll);

                // call the method (same as open button)
                ShowDicom(file);
            }
            catch (Exception ex)
            {
                InfoText.Text = "Download failed";
                MessageBox.Show($"Could not download the image:\n{ex.Message}", "Error");
                throw;
            }
            finally
            { 
                DownloadButton.IsEnabled = true; 
            }

        }

        private void ShowDicom(DicomFile file)
        {
            DicomDataset ds = file.Dataset;

            string patientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, "(none)");
            string modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "(none)");
            string studyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, "(none)");

            // Turn the pixel data into a picture
            var dicomImage = new DicomImage(ds);
            using IImage rendered = dicomImage.RenderImage();
            byte[] pixels = rendered.AsBytes(); // 4 bytes per pixel: Blue, Green, Red, Alpha

            ScanImage.Source = BitmapSource.Create(
                rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgra32, null, pixels, rendered.Width * 4);

            // Show information under the image
            InfoText.Text =
                $"Patient: {patientName} Modality: {modality}, StudyDate: {studyDate}\n" +
                $"Size: {dicomImage.Width} x {dicomImage.Height} pixels, Frames: {dicomImage.NumberOfFrames}";
        }
    }
}