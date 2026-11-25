using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DicomViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // The servers in the dropdown
        private static readonly PacsServer[] Servers =
        {
            new PacsServer("Orthanc demo (Belgium)",
                           "https://orthanc.uclouvain.be/demo/dicom-web",
                           "https://orthanc.uclouvain.be/demo/wado",
                           RetrieveMethod.WadoUri),
            new PacsServer("NCI Imaging Data Commons",
                           "https://proxy.imaging.datacommons.cancer.gov/current/viewer-only-no-downloads-see-tinyurl-dot-com-slash-3j3d9jyp/dicomWeb",
                           "",  // IDC has no WADO-URI
                           RetrieveMethod.WadoRs),
        };

        // One HttpClient for the whole app (creating a new one per request is a known bad habit)
        internal static readonly HttpClient Http = new HttpClient();

        // One slice of the series: its number, its DICOM data and its picture
        //private record   Slice(int Number, DicomDataset Dataset, BitmapSource Picture);

        // One slice of the series. It starts as a placeholder and is filled in when its image arrives,
        // so it changes over time: a class, not a record. This is to implement lazy loading
        private class Slice
        {
            public int Number; // Instance Number (0020,0013), for sorting
            public string InstanceUid = ""; //SOP Instance UID (0008, 0018), for downloading
            public DicomDataset? Dataset;   // null until the image has arrived
            public BitmapSource? Picture;   // null until the image has arrived
            public bool Failed; // true if downloading of rendering failed
        }

        // Dark grey background for thumbnais whose image hasn't arrived yet
        private static readonly Brush PlaceholderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 40));

        // The whole series, in order. The grid, the filmstrip and < > all use this one list.
        private readonly List<Slice> _slices = new();

        // Which slice is shown large (position in _slices). -1 means none.
        private int _current = -1;

        // Window/level chosen by the user (null = use the values stored in the file)
        private double? _windowCenter;
        private double? _windowWidth;

        // The large image is a DicomImage, so we can re-render it with other window/level values
        private DicomImage? _currentImage;

        // Remembered while the right mouse button is held down
        private bool _isAdjusting;
        private Point _dragStart;
        private double _startCenter;
        private double _startWidth;

        public MainWindow()
        {
            InitializeComponent();

            ServerBox.ItemsSource = Servers; // put the servers in the dropdown, (no server preselected: the user chooses)
        }

        private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BrowseButton.IsEnabled = ServerBox.SelectedItem != null;
            ServerPlaceholder.Visibility = ServerBox.SelectedItem == null
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // Opens the Studies window; if the user pick a series, download it
        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var server = (PacsServer)ServerBox.SelectedItem;
            var browser = new StudyBrowserWindow(server) { Owner = this };
            if (browser.ShowDialog() != true)
                return; // closed without choosing a series

            await DownloadSeriesAsync(server, browser.SelectedStudyUid, browser.SelectedSeriesUid);
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

        private async Task DownloadSeriesAsync(PacsServer server, string studyUid, string seriesUid)
        {
            BrowseButton.IsEnabled = false; // stop double clicks while downloading
            OpenButton.IsEnabled = false;   // opening a file would clear the series mid-download
            ResetSeries();  // the old series gets cleared, whatever happens next
            InfoText.Text = "Asking the PACS for the list of images...";

            try
            {
                // 1. QIDO-RS: ask for the list of images in the series (same as in the browser)
                string listUrl = $"{server.DicomWebRoot}/studies/{studyUid}/series/{seriesUid}/instances?limit=1000";
                string json = await Http.GetStringAsync(listUrl);

                // 2. One place holder per image, with its UID and Instance Number from the list
                var placeholders = new List<Slice>();

                if (!string.IsNullOrWhiteSpace(json))
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    foreach (JsonElement item in doc.RootElement.EnumerateArray())
                    {
                        int number = int.TryParse(DicomJson.GetText(item, "00200013"), out int n)
                            ? n : placeholders.Count + 1; // no number in the list: keep the server's order
                        placeholders.Add(new Slice
                        {
                            InstanceUid = DicomJson.GetText(item, "00080018"),
                            Number = number
                        });
                    }
                }

                if (placeholders.Count == 0)
                {
                    InfoText.Text = "The server returned no images for this series.";
                    return;
                }

                // 3. Sort BEFORE downloading, and show the grid of placeholders straight away
                _slices.AddRange(placeholders.OrderBy(s => s.Number));
                BuildThumbnails();
                HeaderTitle.Text = "Loading series...";
                HeaderDetails.Text = "";
                ShowGridMode();

                // 4. Download each image in order, and fill its placeholder as soon as it arrives
                int failed = 0;
                string lastError = "";
                bool headerDone = false;

                for (int i = 0; i < _slices.Count; i++)
                {
                    Slice slice = _slices[i];
                    InfoText.Text = $"Downloading image {i + 1} / {_slices.Count}..." +
                                    (failed > 0 ? $"  ({failed} failed)" : "");

                    try
                    {
                        byte[] data = await DownloadInstanceAsync(server, studyUid, seriesUid, slice.InstanceUid);

                        using var stream = new MemoryStream(data);
                        slice.Dataset = DicomFile.Open(stream, FileReadOption.ReadAll).Dataset;
                        slice.Picture = Render(slice.Dataset);

                        if (!headerDone)
                        {
                            BuildHeader();
                            headerDone = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        lastError = $"{ex.GetType().Name}: {ex.Message}";
                        slice.Failed = true;
                    }

                    FillThumbnail(i); // show the picture, or mark it as failed
                }

                // 5. Final message
                int loaded = _slices.Count(s => s.Picture != null);

                if (loaded == 0)
                {
                    InfoText.Text = "Download failed";
                    MessageBox.Show($"None of the {_slices.Count} images could be shown.\n\n" +
                        $"Last error:\n{lastError}", "Error");
                    ResetSeries();
                    return;
                }

                InfoText.Text = failed > 0 ?
                    $"{loaded} of {_slices.Count} images shown; {failed} could not be downloaded or displayed. Last error: {lastError}"
                    : $"{loaded} slices. Click one to open it.";

            }
            catch (Exception ex)
            {
                InfoText.Text = "Download failed";
                MessageBox.Show($"Could not download the series:\n{ex.GetType().Name}: {ex.Message}", "Error");
            }
            finally
            {
                BrowseButton.IsEnabled = true;
                OpenButton.IsEnabled = true;
            }
        }

        // Downloads one image (instance) as DICOM bytes, using the server's retrieve method
        private static async Task<byte[]> DownloadInstanceAsync(PacsServer server, string studyUid, string seriesUid, string instanceUid)
        {
            // WADO-URI: one address wtih parameters; the answer is the plain .dcm file
            if (server.Retrieve == RetrieveMethod.WadoUri)
            {
                string url = $"{server.WadoUriRoot}?requestType=WADO" +
                    $"&studyUID={studyUid}&seriesUID={seriesUid}" +
                    $"&objectUID={instanceUid}&contentType=application/dicom";
                return await Http.GetByteArrayAsync(url);
            }

            // WADO-RS: the address is a path; the format we want goes int the "Accept" header
            string rsUrl = $"{server.DicomWebRoot}/studies/{studyUid}/series/{seriesUid}/instances/{instanceUid}";

            using var request = new HttpRequestMessage(HttpMethod.Get, rsUrl);
            request.Headers.TryAddWithoutValidation("Accept",
                "multipart/related; type=\"application/dicom\"; transfer-syntax=1.2.840.10008.1.2.1");

            using HttpResponseMessage response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode(); // turn HTTP errors (404, 500...) into exceptions
            byte[] body = await response.Content.ReadAsByteArrayAsync();

            // Some servers send the plain file anyway; then there is nothing to unwrap
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType != "multipart/related")
                return body;

            // The "boundary" is the separatr text between the parts of the package
            string boundary = response.Content.Headers.ContentType!.Parameters
                .First(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)).Value!.Trim('"');

            return ExtractFirstPart(body, boundary);
        }

        // A multipart answer looks like this:
        //  --boundary
        // Content-Type: aplication/dicom
        // (empty line)
        // ...the DICOM file bytes...
        // --boundary--
        // This method returns just the file bytes
        private static byte[] ExtractFirstPart(byte[] body, string boundary)
        {
            byte[] firstBoundary = Encoding.ASCII.GetBytes("--" + boundary);
            byte[] headersEnd = Encoding.ASCII.GetBytes("\r\n\r\n"); // the empty
            byte[] nextBoundary = Encoding.ASCII.GetBytes("\r\n--" + boundary);

            int boundaryPos = IndexOf(body, firstBoundary, 0);
            if (boundaryPos < 0)
                throw new InvalidDataException("Multipart answer has no boundary");

            int dataStart = IndexOf(body, headersEnd, boundaryPos);
            if (dataStart < 0)
                throw new InvalidDataException("Multipart answer has no headers end");
            dataStart += headersEnd.Length; // skip the empty line itself

            int dataEnd = IndexOf(body, nextBoundary, dataStart);
            if (dataEnd < 0)
                throw new InvalidDataException("Multipart answer has no closing boundary");

            return body[dataStart..dataEnd];

        }

        // Finds a sequesnce of bytes inside a larger array, starting at 'start'. Returns -1 if not found
        private static int IndexOf(byte[] data, byte[] pattern, int start)
        {
            int position = data.AsSpan(start).IndexOf(pattern);
            return position < 0 ? -1 : position + start;
        }

        // Turns the DICOM pixel data into a picture WPF can show,
        // using the window/level stored in the file.
        // Used for opening files, downloaded slices and thumbnails.
        private static BitmapSource Render(DicomDataset ds)
        {
            return Render(new DicomImage(ds));
        }

        // Renders an existing DicomImage, keeping any window/level changes made to it.
        // A new DicomImage always starts with the file's values, so for window/level we keep
        // one DicomImage (_currentImage), change its WindowCenter/WindowWidth, and re-render it here.
        // The method above also uses this one, so the drawing code exists only once.
        private static BitmapSource Render(DicomImage dicomImage)
        {
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
                    // Background = Brushes.Transparent, // makes the whole box clickable
                    Background = _slices[i].Picture == null ? PlaceholderBrush : Brushes.Transparent, // grey = not arrived yet; any background makes the box clickable
                    Cursor = Cursors.Hand,
                    Tag = i // rememeber its position in _slices
                };
                thumbnail.MouseLeftButtonUp += Thumbnail_Click;

                ThumbnailPanel.Children.Add(thumbnail);
            }
        }

        // Updates one thumbnail when its image has arrived (or failed)
        private void FillThumbnail(int position)
        {
            Slice slice = _slices[position];
            var thumbnail = (Border)ThumbnailPanel.Children[position];
            var content = (StackPanel)thumbnail.Child;
            var picture = (Image)content.Children[0];   // the picture is the first child...
            var label = (TextBlock)content.Children[1];  // ...and the number is the second

            if (slice.Picture != null)
            {
                picture.Source = slice.Picture;
                thumbnail.Background = Brushes.Transparent; // no longer a placeholder
            }
            else if (slice.Failed)
            {
                label.Text = $"{slice.Number} X";
                label.Foreground = Brushes.IndianRed;
            }
        }

        // Fills the header above the grid with information about the series
        private void BuildHeader()
        {
            // Patient, study and series information is the same in every slice, so use the first
            DicomDataset ds = _slices.First(s => s.Dataset != null).Dataset!;

            // Turn the DICOM modality code into a name people know
            string modality = ds.GetSingleValueOrDefault(DicomTag.Modality, "");
            string modalityName = modality switch
            {
                "MR" => "MRI",
                "CT" => "CT",
                "PT" => "PET",
                "US" => "Ultrasound",
                "CR" or "DX" => "X-ray",
                _ => modality
            };

            string series = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, "");
            string patient = ds.GetSingleValueOrDefault(DicomTag.PatientName, "unknown");
            string patientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, "-");
            string born = FormatDate(ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, ""));
            string studyDate = FormatDate(ds.GetSingleValueOrDefault(DicomTag.StudyDate, ""));

            HeaderTitle.Text = $"{modalityName} · {series}";
            HeaderDetails.Text = $"Patient: {patient}   ID: {patientId}   Born: {born}   " +
                $"Study date: {studyDate}   {_slices.Count} images";
        }

        //DICOM dates are written "20061201"; this shows them as "2006-12-01"
        internal static string FormatDate(string dicomDate)
        {
            if (DateTime.TryParseExact(dicomDate, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime date))
                return date.ToString("yyyy-MM-dd");

            return dicomDate == "" ? "-" : dicomDate; // empty or unusual: show it as it is
        }

        // One click on a thumbnail: open that slice in split mode
        private void Thumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            int position = (int)((Border)sender).Tag;
            if (_slices[position].Picture == null)  // not arrived yet (or failed)
            {
                e.Handled = true;
                return;
            }

            ShowSplitMode();
            ShowSlice(position);
            e.Handled = true; // Don't let the click reach the empty-space handler
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

            // The large picture: with the user's window/level if set, otherwise the ready-made picture
            //ScanImage.Source = _slices[_current].Picture;
            _currentImage = new DicomImage(_slices[_current].Dataset!);
            if (_windowCenter.HasValue && _windowWidth.HasValue)
            {
                _currentImage.WindowCenter = _windowCenter.Value;
                _currentImage.WindowWidth = _windowWidth.Value;
                ScanImage.Source = Render(_currentImage);
            }
            else
            {
                ScanImage.Source = _slices[_current].Picture;
            }


            InfoText.Text = Describe(_slices[_current].Dataset!);
            NavigationLayer.Visibility = Visibility.Visible;
            UpdateCounter();
            //SliceCounter.Text = $"Slice {_current + 1} / {_slices.Count}";

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

            _windowCenter = null; // forget the user's window/level...
            _windowWidth = null; // ...so the next series starts with its own values
            _currentImage = null; // no image is being adjusted anymore
            _isAdjusting = false; // never start a new series in the middle of a drag
            ScanImage.Source = null;    // remove the old large image
            HeaderTitle.Text = "";  // remove the old header
            HeaderDetails.Text = "";
            GridHeader.Visibility = Visibility.Collapsed;

        }

        // ---- The three view modes: only the column width change ----

        // Mode 1: thumbnails use all the space
        private void ShowGridMode()
        {
            ThumbnailColumn.Width = new GridLength(1, GridUnitType.Star);
            SliceColumn.Width = new GridLength(0);
            BackToGridButton.Visibility = Visibility.Collapsed;
            GridHeader.Visibility = Visibility.Visible;
            InfoText.Text = $"{_slices.Count} slices. Click one to open it.";
        }

        // Mode 2: filmstrip on the left, large slice on the right
        private void ShowSplitMode()
        {
            ThumbnailColumn.Width = new GridLength(160);
            SliceColumn.Width = new GridLength(1, GridUnitType.Star);
            BackToGridButton.Visibility = Visibility.Visible;
            GridHeader.Visibility = Visibility.Collapsed;

            SliceView.Cursor = Cursors.Hand;
            SliceView.ToolTip = "Double-click for full view";
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
            while (target >= 0 && target < _slices.Count && _slices[target].Picture == null)
                target += step; // skip slices that haven't arrived (or failed)
            if (target < 0 || target >= _slices.Count)
                return; // already at the first or last slice

            ShowSlice(target);
        }

        // Shows the slice number and the current window/level under the image
        private void UpdateCounter()
        {
            string windowLevel = _currentImage == null
                ? ""
                : $"  ·  W {_currentImage.WindowWidth:0} L {_currentImage.WindowCenter:0}";
            SliceCounter.Text = $"Slice {_current + 1} / {_slices.Count}{windowLevel}";
        }

        // Right button down: start adjusting
        private void SliceView_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentImage == null)
                return; // no series open

            _isAdjusting = true;
            _dragStart = e.GetPosition(SliceView);
            _startCenter = _currentImage.WindowCenter;
            _startWidth = _currentImage.WindowWidth;

            SliceView.CaptureMouse();   // keep getting mouse moves even outside the image
            e.Handled = true;
        }

        // Mouse moves while the right button is down: change the contrast
        private void SliceView_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isAdjusting || _currentImage == null)
                return;

            // The right button was released while we didn't notice (e.g Alt+Tab): stop adjusting
            if (e.RightButton != MouseButtonState.Pressed)
            {
                _isAdjusting = false;
                SliceView.ReleaseMouseCapture();
                return;
            }

            Point now = e.GetPosition(SliceView);
            double dx = now.X - _dragStart.X;   // right = wider window (softer contrast)
            double dy = now.Y - _dragStart.Y;   // down = higher level (darker image)

            // Move faster for wide windows (CT) and slower for narrow ones (MR)
            double speed = _startWidth / 200;

            _windowWidth = Math.Max(1, _startWidth + dx * speed);
            _windowCenter = _startCenter + dy * speed;

            _currentImage.WindowWidth = _windowWidth.Value;
            _currentImage.WindowCenter = _windowCenter.Value;
            ScanImage.Source = Render(_currentImage);
            UpdateCounter();
        }

        // Right button up: stop adjusting
        private void SliceView_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isAdjusting)
                return;

            _isAdjusting = false;
            SliceView.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void BackToGridButton_Click(object sender, RoutedEventArgs e)
        {
            ShowGridMode();
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
            MoveBy(e.Delta > 0 ? -1 : +1);
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
            if (e.Key == Key.Left || e.Key == Key.Up)
            {
                MoveBy(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Right || e.Key == Key.Down)
            {
                MoveBy(+1);
                e.Handled = true;
            }
            else if (e.Key == Key.R && _current >= 0)
            {
                _windowCenter = null;
                _windowWidth = null;
                ShowSlice(_current);
                e.Handled = true;
            }
        }

        // Click on empty space in the grid: remove the selection
        private void ThumbnailView_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool isGrid = SliceColumn.Width.Value == 0;
            if (!isGrid || _current < 0)
                return; // only in Grid mode, and only if something is selected

            ((Border)ThumbnailPanel.Children[_current]).BorderBrush = Brushes.Transparent;
            _current = -1;  // nothing selected any more
        }
    }
}