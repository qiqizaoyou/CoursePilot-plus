using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace CoursePilot
{
    public sealed class QuizText
    {
        public string Text;
        public Rectangle Bounds;
    }

    public sealed class QuizTextPage
    {
        public readonly List<QuizText> Lines = new List<QuizText>();
        public readonly List<QuizText> Words = new List<QuizText>();
        internal readonly List<QuizChoiceReading> ChoiceReadings = new List<QuizChoiceReading>();
        internal readonly List<string> ChoiceDiagnostics = new List<string>();
    }

    public interface IQuizOcr
    {
        Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation);
    }

    internal static class QuizBodyOcr
    {
        internal static async Task<QuizTextPage> Read(Bitmap image, IQuizOcr ocr, CancellationToken cancellation)
        {
            QuizTextPage page = await QuizSubmissionVision.ReadText(image, ocr, cancellation).ConfigureAwait(false);
            foreach (QuizText line in page.Lines)
            {
                if (line.Bounds.Left > image.Width * .26) continue;
                string type = QuizVision.UnnumberedType(line.Text);
                if (type == null) continue;

                cancellation.ThrowIfCancellationRequested();
                int margin = Math.Max(12, Math.Min(60, line.Bounds.Height));
                Rectangle region = Rectangle.Intersect(new Rectangle(Point.Empty, image.Size), Rectangle.FromLTRB(
                    line.Bounds.Left - 2 * margin, line.Bounds.Top - margin,
                    Math.Min(line.Bounds.Right + 2 * margin, line.Bounds.Left + 20 * margin), line.Bounds.Bottom + margin));
                if (region.Width < 1 || region.Height < 1) continue;
                using (Bitmap crop = image.Clone(region, PixelFormat.Format24bppRgb))
                using (Bitmap enlarged = new Bitmap(crop, crop.Width * 2, crop.Height * 2))
                {
                    QuizTextPage reread = await ocr.Read(enlarged, cancellation).ConfigureAwait(false);
                    string number = null;
                    int left = line.Bounds.Left;
                    int centerY = line.Bounds.Top + line.Bounds.Height / 2;
                    foreach (QuizText row in reread.Lines)
                    {
                        QuizMarker marker = QuizVision.Marker(row, 0, enlarged.Width);
                        if (marker == null) continue;
                        string tag = QuizVision.Normalize(row.Text).Substring(marker.Number.Length).TrimStart('.', '、');
                        int rereadLeft = region.Left + row.Bounds.Left / 2;
                        int rereadY = region.Top + (row.Bounds.Top + row.Bounds.Height / 2) / 2;
                        if (QuizVision.UnnumberedType(tag) != type || Math.Abs(rereadY - centerY) > margin / 2 ||
                            rereadLeft >= line.Bounds.Left - margin / 4) continue;
                        if (number != null) { number = null; break; }
                        number = marker.Number;
                        left = rereadLeft;
                    }
                    if (number != null)
                    {
                        line.Text = number + " " + line.Text;
                        line.Bounds = Rectangle.FromLTRB(left, line.Bounds.Top, line.Bounds.Right, line.Bounds.Bottom);
                    }
                }
            }
            await QuizChoiceOcr.Recover(image, page, ocr, cancellation).ConfigureAwait(false);
            return page;
        }
    }

    public sealed class WindowsQuizOcr : IQuizOcr
    {
        [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMemoryBufferByteAccess
        {
            void GetBuffer(out IntPtr buffer, out uint capacity);
        }
        public static string LanguageStatus()
        {
            return OcrEngine.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                ? "中文文字识别可用" : "请在 Windows 语言选项安装中文「光学字符识别」";
        }

        public async Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
            if (language == null) throw new QuizPauseException("未安装 Windows 中文文字识别，请在系统语言选项安装中文「光学字符识别」后重试。");
            OcrEngine engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine == null) throw new QuizPauseException("Windows 中文文字识别未能启动。");
            if (image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension)
                throw new QuizPauseException("截图超过 Windows 文字识别尺寸限制，请缩小识别区域。");
            byte[] pixels = new byte[image.Width * image.Height * 4];
            using (Bitmap readable = QuizVision.OcrInput(image))
            using (Bitmap copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(copy))
                    graphics.DrawImage(readable, new Rectangle(Point.Empty, copy.Size), new Rectangle(Point.Empty, readable.Size), GraphicsUnit.Pixel);
                BitmapData data = copy.LockBits(new Rectangle(Point.Empty, copy.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < copy.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * copy.Width * 4, copy.Width * 4);
                }
                finally { copy.UnlockBits(data); }
            }
            using (SoftwareBitmap software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Ignore))
            {
                using (BitmapBuffer buffer = software.LockBuffer(BitmapBufferAccessMode.Write))
                using (var reference = buffer.CreateReference())
                {
                    IntPtr pointer; uint capacity;
                    ((IMemoryBufferByteAccess)reference).GetBuffer(out pointer, out capacity);
                    var plane = buffer.GetPlaneDescription(0);
                    if ((long)plane.StartIndex + (long)(image.Height - 1) * plane.Stride + image.Width * 4 > capacity)
                        throw new QuizPauseException("Windows 图片缓冲区尺寸异常。");
                    for (int y = 0; y < image.Height; y++)
                        Marshal.Copy(pixels, y * image.Width * 4, IntPtr.Add(pointer, plane.StartIndex + y * plane.Stride), image.Width * 4);
                }
                var operation = engine.RecognizeAsync(software);
                TaskCompletionSource<OcrResult> completion = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                operation.Completed = delegate(Windows.Foundation.IAsyncOperation<OcrResult> finished, Windows.Foundation.AsyncStatus status)
                {
                    try
                    {
                        if (status == Windows.Foundation.AsyncStatus.Canceled) completion.TrySetCanceled();
                        else if (status == Windows.Foundation.AsyncStatus.Error) completion.TrySetException(new QuizPauseException("Windows 文字识别失败，请重新截图。"));
                        else completion.TrySetResult(finished.GetResults());
                    }
                    catch (Exception exception) { completion.TrySetException(exception); }
                };
                OcrResult result;
                using (cancellation.Register(delegate { try { operation.Cancel(); } catch { } }))
                {
                    try { result = await completion.Task.ConfigureAwait(false); }
                    finally { operation.Close(); }
                }
                cancellation.ThrowIfCancellationRequested();
                QuizTextPage page = new QuizTextPage();
                foreach (OcrLine line in result.Lines)
                {
                    Rectangle bounds = Rectangle.Empty;
                    foreach (OcrWord word in line.Words)
                    {
                        var box = word.BoundingRect;
                        Rectangle rectangle = Rectangle.FromLTRB((int)Math.Floor(box.Left), (int)Math.Floor(box.Top), (int)Math.Ceiling(box.Right), (int)Math.Ceiling(box.Bottom));
                        page.Words.Add(new QuizText { Text = word.Text, Bounds = rectangle });
                        bounds = bounds.IsEmpty ? rectangle : Rectangle.Union(bounds, rectangle);
                    }
                    if (!bounds.IsEmpty) page.Lines.Add(new QuizText { Text = line.Text, Bounds = bounds });
                }
                // OCR may split the option-letter column from the option-text column.
                // Reassemble horizontal rows from word geometry before matching scroll anchors.
                List<List<QuizText>> rows = new List<List<QuizText>>();
                foreach (QuizText word in page.Words.OrderBy(w => w.Bounds.Top + w.Bounds.Height / 2.0).ThenBy(w => w.Bounds.Left))
                {
                    double center = word.Bounds.Top + word.Bounds.Height / 2.0;
                    List<QuizText> row = rows.OrderBy(r => Math.Abs(r.Average(w => w.Bounds.Top + w.Bounds.Height / 2.0) - center))
                        .FirstOrDefault(r => Math.Abs(r.Average(w => w.Bounds.Top + w.Bounds.Height / 2.0) - center) <= Math.Max(4, Math.Max(word.Bounds.Height, r.Max(w => w.Bounds.Height)) * .55));
                    if (row == null) { row = new List<QuizText>(); rows.Add(row); }
                    row.Add(word);
                }
                page.Lines.Clear();
                foreach (List<QuizText> row in rows.OrderBy(r => r.Min(w => w.Bounds.Top)))
                {
                    Rectangle bounds = row[0].Bounds;
                    foreach (QuizText word in row.Skip(1)) bounds = Rectangle.Union(bounds, word.Bounds);
                    page.Lines.Add(new QuizText { Text = String.Join(" ", row.OrderBy(w => w.Bounds.Left).Select(w => w.Text)), Bounds = bounds });
                }
                return page;
            }
        }
    }

    public class QuizPauseException : Exception
    {
        public QuizPauseException(string message) : base(message) { }
    }
}
