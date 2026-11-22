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
                ResetSeries(); // a single file has no series: hide thumbnails and < >
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
                // _slices.Clear();
                ResetSeries();
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
                //ScanImage.Source = _slices[0].Picture;
                //InfoText.Text = $"Downloaded {_slices.Count} slices. First is number {_slices[0].Number}, " +
                //$"last is number {_slices[^1].Number}.";
                // 5. Make one thumbnail per slice and show the grid
                BuildThumbnails();
                ShowGridModel();
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

        private void BuildThumbnails()
        {
            ThumbnailPanel.Children.Clear();

            for (int i = 0; i < _slices.Count; i++)
            {
                // the small picture, with the slice number under it
                var picture = new Image
                {
                    Source = _slices[i].Picture,
                    Width = 110,
                    Height = 110,
                    Stretch = Stretch.Uniform
                };

                var label = new TextBlock
                {
                    Text = _slices[i].Number.ToString(),
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var content = new StackPanel();
                content.Children.Add(picture);
                content.Children.Add(label);

                // A border around it: transparent now, orange when it is the current slice
                var thumbnail = new Border
                { 
                    Child = content,
                    Margin = new Thickness(3),
                    Padding = new Thickness(2),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Background = Brushes.Transparent, // makes the whole box clickable
                    Cursor = Cursors.Hand,
                    Tag = i // rememeber its position in _slices
                };
                thumbnail.MouseLeftButtonUp += Thumbnail_Click;

                ThumbnailPanel.Children.Add(thumbnail);
            }
        }

        // One click on a thumbnail: open that slice in split mode
        private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            int position = (int)((Border)sender).Tag;
            ShowSplitMode();
            ShowSlice(position);
        }

        // Shows the slice at this position large, and highlights its thumbnail
        private void ShowSlice(int position)
        {
            // Remove the highlight from the previous thumbnail
            if (_current >= 0)
                ((Border)ThumbnailPanel.Children[_current]).BorderBrush = Brushes.Transparent;

            _current = position;

            // Highlight the new one and scroll the filmstrip so it is visible
            var thumbnail = (Border)ThumbnailPanel.Children[_current];
            thumbnail.BorderBrush = Brushes.Orange;
            thumbnail.BringIntoView();

            // The large picture, the text and the counter
            ScanImage.Source = _slices[_current].Picture;
            InfoText.Text = Describe(_slices[_current].Dataset);
            NavigationLayer.Visibility = Visibility.Visible;
            SliceCounter.Text = $"Slice {_current + 1} / {_slices.Count}";

            // No < on the first slice, no > on the last
            PreviousButton.IsEnabled = _current > 0;
            NextButton.IsEnabled = _current < _slices.Count - 1;
        }

        // Forgets the old series (used before a new download or a single file)
        private void ResetSeries()
        {
            _slices.Clear();
            ThumbnailPanel.Children.Clear();
            _current = -1;

            ThumbnailColumn.Width = new GridLength(0);
            SliceColumn.Width = new GridLength(1, GridUnitType.Star);
            NavigationLayer.Visibility = Visibility.Collapsed;
            BackToGridButton.Visibility = Visibility.Collapsed;

            SliceView.Cursor = null;
            SliceView.ToolTip = null;
        }

        // ---- The three view modes: only the column width change ----
        
        // Mode 1: thumbnails use all the space
        private void ShowGridModel()
        {
            ThumbnailColumn.Width = new GridLength(1, GridUnitType.Star);
            SliceColumn.Width = new GridLength(0);
            BackToGridButton.Visibility = Visibility.Collapsed;
            InfoText.Text = $"{_slices.Count} slices. Click one to open it.";
        }

        // Mode 2: filmstrip on the left, large slice on the right
        private void ShowSplitMode()
        {
            ThumbnailColumn.Width = new GridLength(160);
            SliceColumn.Width = new GridLength(1, GridUnitType .Star);
            BackToGridButton.Visibility = Visibility.Visible;

            SliceView.Cursor = Cursors.Hand;
            SliceView.ToolTip = "Double-click to show the filmstrip";
        }

        // Mode 3: only the large slice, using all the space
        private void ShowFullMode()
        {
            ThumbnailColumn.Width = new GridLength(0);
            SliceColumn.Width = new GridLength(1, GridUnitType.Star);
            BackToGridButton.Visibility = Visibility.Visible;

            SliceView.Cursor = Cursors.Hand;
            SliceView.ToolTip = "Double-click to show the filmstrip";
        }

        // Moves forward (+1) or back(-1) in the list, if possible
        private void MoveBy(int step)
        {
            if (_current < 0)
                return; // no series open

            int target = _current + step;
            if (target < 0 || target >= _slices.Count)
                return; // already at the first or last slice

            ShowSlice(target);
        }

        // Events that still need to be implemented
        private void BackToGridButton_Click(object sender, RoutedEventArgs e) 
        {
            ShowGridModel();
        }

        // ---- Navigation ----

        // < and > buttons
        private void PreviousButton_Click(object sender, RoutedEventArgs e) 
        { 
            MoveBy(-1);
        }
        private void NextButton_Click(object sender, RoutedEventArgs e) 
        {
            MoveBy(+1);
        }

        // Mouse wheel: up = previous slice, down = next slice
        private void SliceView_MouseWheel(object sender, MouseWheelEventArgs e) 
        { 
            MoveBy(e.Delta > 0  ? -1 : +1);
            e.Handled = true;
        }

        // Double click on the large slice: switch between Split and Full mode
        private void SliceView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) 
        { 
            if (e.ClickCount != 2 || _current < 0)
                return;     // not a double click, or no series open

            bool isFull = ThumbnailColumn.Width.Value == 0;
            if (isFull)
            {
                ShowSplitMode();
                ShowSlice(_current);    // scroll the filmstrip back to the current slice
            }
            else
            {
                ShowFullMode();
            }
        }

        // Arrow keys: left/up = previous, right/down = next
        private void Window_KeyDown(object sender, KeyEventArgs e) 
        {
            if(e.Key == Key.Left || e.Key == Key.Up)
            {
                MoveBy(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Right || e.Key == Key.Down)
            {
                MoveBy(+1);
                e.Handled = true;
            }
        }
    }
}