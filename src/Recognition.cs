using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CoursePilot
{
    public enum DotColor { Yellow, Green }

    public sealed class Dot
    {
        public DotColor Color;
        public Rectangle Bounds;
        public Point Center { get { return new Point(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2); } }
    }

    public struct Region
    {
        public double X, Y, Width, Height;
        public Region(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
        public Rectangle ToPixels(Size screen)
        {
            int left = Math.Max(0, Math.Min(screen.Width - 1, (int)Math.Round(X * screen.Width)));
            int top = Math.Max(0, Math.Min(screen.Height - 1, (int)Math.Round(Y * screen.Height)));
            int right = Math.Max(left + 1, Math.Min(screen.Width, (int)Math.Round((X + Width) * screen.Width)));
            int bottom = Math.Max(top + 1, Math.Min(screen.Height, (int)Math.Round((Y + Height) * screen.Height)));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
    }

    public sealed class Regions
    {
        public Region List = new Region(0.196, 0.251, 0.801, 0.691);
        public Region Status = new Region(0.196, 0.171, 0.215, 0.073);
        public Region Task = new Region(0.150, 0.231, 0.022, 0.030);
        public Region Video = new Region(0.155, 0.272, 0.584, 0.491);
        public Region Back = new Region(0.124, 0.091, 0.041, 0.027);
        public Region QuizHeader = new Region(0.099, 0.1406, 0.641, 0.1745);
        public Region QuizBody = new Region(0.153, 0.1406, 0.589, 0.802);
        public Regions Clone() { return new Regions { List = List, Status = Status, Task = Task, Video = Video, Back = Back, QuizHeader = QuizHeader, QuizBody = QuizBody }; }
    }

    public static class Recognition
    {
        // Components are measured on a compact working image. White tick marks inside
        // green circles are deliberately retained as holes rather than separate dots.
        public static List<Dot> FindDots(Bitmap input, bool chapterList)
        {
            List<Dot> dots = FindDotsCore(input, chapterList, false);
            return chapterList ? DirectoryColumn(input, dots) : dots;
        }

        public static List<Dot> FindTaskDots(Bitmap input)
        {
            return FindDotsCore(input, false, true);
        }

        internal static List<Dot> TaskCandidates(Bitmap input) { return FindDotsCore(input, false, true, false); }
        internal static void MarkTaskCandidates(Bitmap image)
        {
            List<Dot> accepted = FindTaskDots(image), candidates = TaskCandidates(image);
            using (Graphics g = Graphics.FromImage(image))
                foreach (Dot dot in candidates)
                using (Pen pen = new Pen(accepted.Any(d => d.Bounds == dot.Bounds) ? Color.LimeGreen : Color.Red, 2)) g.DrawRectangle(pen, dot.Bounds);
        }
        private static bool RoundTask(int[] queue, int count, int stride, int left, int top, int width, int height)
        {
            bool[] ink = new bool[width * height];
            for (int i = 0; i < count; i++) ink[(queue[i] / stride - top) * width + queue[i] % stride - left] = true;
            int ring = 0, filled = 0, outside = 0;
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                double dx = (x + .5 - width / 2.0) / (width / 2.0), dy = (y + .5 - height / 2.0) / (height / 2.0), radius = dx * dx + dy * dy;
                bool pixel = ink[y * width + x];
                if (radius >= .30 && radius <= .72) { ring++; if (pixel) filled++; }
                if (radius > 1.12 && pixel) outside++;
            }
            // Inspect the middle ring so a white check mark can remain inside a green circle.
            return ring > 0 && filled >= ring * .70 && outside <= count * .10;
        }
        private static List<Dot> FindDotsCore(Bitmap input, bool chapterList, bool taskPoint, bool roundTask = true)
        {
            double scale = Math.Min(1.0, 1920.0 / input.Width);
            int width = Math.Max(1, (int)Math.Round(input.Width * scale));
            int height = Math.Max(1, (int)Math.Round(input.Height * scale));
            using (Bitmap small = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(small)) graphics.DrawImage(input, 0, 0, width, height);
                BitmapData data = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                byte[] pixels = new byte[data.Stride * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                small.UnlockBits(data);
                byte[] mask = new byte[width * height];
                int scanWidth = width;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < scanWidth; x++)
                    {
                        int offset = y * data.Stride + x * 3;
                        int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                        if (g >= 105 && g > r * 1.20 && g > b * 1.15) mask[y * width + x] = 2;
                        else if (r >= 160 && g >= 100 && g <= r * 0.94 && b <= g * 0.82 && r - b >= 65) mask[y * width + x] = 1;
                    }
                List<Dot> result = new List<Dot>();
                int[] queue = new int[width * height];
                int minSize = Math.Max(4, (int)Math.Round((taskPoint ? Math.Min(input.Width, input.Height) * 0.12 : 8) * scale));
                int maxSize = Math.Max(minSize + 3, (int)Math.Round((taskPoint ? Math.Min(input.Width, input.Height) * 0.95 : 140) * scale));
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < scanWidth; x++)
                    {
                        int index = y * width + x;
                        byte color = mask[index];
                        if (color == 0) continue;
                        mask[index] = 0;
                        int head = 0, tail = 1, left = x, right = x, top = y, bottom = y;
                        queue[0] = index;
                        while (head < tail)
                        {
                            int current = queue[head++], cy = current / width, cx = current % width;
                            left = Math.Min(left, cx); right = Math.Max(right, cx); top = Math.Min(top, cy); bottom = Math.Max(bottom, cy);
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = cx + dx, ny = cy + dy;
                                    if (nx < 0 || nx >= scanWidth || ny < 0 || ny >= height) continue;
                                    int neighbor = ny * width + nx;
                                    if (mask[neighbor] == color) { mask[neighbor] = 0; queue[tail++] = neighbor; }
                                }
                        }
                        int w = right - left + 1, h = bottom - top + 1;
                        double aspect = (double)w / h, fill = (double)tail / (w * h);
                        // Reject cropped circles, lettering, broad blocks, and rectangular icons.
                        if (w < minSize || h < minSize || w > maxSize || h > maxSize || aspect < 0.72 || aspect > 1.38 || fill < 0.38 || fill > 0.88) continue;
                        if (left <= 0 || top <= 0 || right >= width - 1 || bottom >= height - 1) continue;
                        if (taskPoint && roundTask && !RoundTask(queue, tail, width, left, top, w, h)) continue;
                        result.Add(new Dot { Color = color == 1 ? DotColor.Yellow : DotColor.Green,
                            Bounds = new Rectangle((int)Math.Round(left / scale), (int)Math.Round(top / scale), (int)Math.Round(w / scale), (int)Math.Round(h / scale)) });
                    }
                result.Sort(delegate(Dot a, Dot b) { return a.Center.Y.CompareTo(b.Center.Y); });
                return result;
            }
        }

        public static Bitmap Crop(Bitmap screen, Region region)
        {
            return screen.Clone(region.ToPixels(screen.Size), PixelFormat.Format24bppRgb);
        }

        // A wider crop is supported, but multiple plausible status columns are ambiguous.
        internal static List<Dot> DirectoryColumn(Bitmap image, List<Dot> candidates)
        {
            List<List<Dot>> columns = new List<List<Dot>>();
            foreach (Dot dot in candidates.OrderBy(d => d.Center.X))
            {
                if (TitleKey(image, dot, true) == null) continue;
                List<Dot> column = columns.FirstOrDefault(c => Math.Abs(c[0].Center.X - dot.Center.X) <= Math.Max(8, c[0].Bounds.Width * .6)
                    && dot.Bounds.Width >= c[0].Bounds.Width * .6 && dot.Bounds.Width <= c[0].Bounds.Width * 1.6);
                if (column == null) { column = new List<Dot>(); columns.Add(column); }
                column.Add(dot);
            }
            if (columns.Count != 1) return new List<Dot>();
            return columns[0].OrderBy(d => d.Center.Y).ToList();
        }

        internal static List<Dot> DirectoryCandidates(Bitmap image) { return FindDotsCore(image, true, false); }

        public static Rectangle ScrollComparisonBounds(Size screen, Region list)
        {
            Rectangle area = list.ToPixels(screen);
            int bottom = Math.Min(area.Bottom, (int)Math.Floor(screen.Height * 0.8));
            return bottom > area.Top ? Rectangle.FromLTRB(area.Left, area.Top, area.Right, bottom) : Rectangle.Empty;
        }

        // Identify a directory row by its title pixels, excluding the colored dot
        // and screen coordinates so the same title can be skipped after scrolling.
        public static string LessonKey(Bitmap list, Dot dot)
        { return TitleKey(list, dot, false); }

        private static string TitleKey(Bitmap list, Dot dot, bool requireTitle)
        {
            int left = Math.Min(list.Width - 1, dot.Bounds.Right + Math.Max(6, (int)Math.Round(dot.Bounds.Width * 0.3)));
            int halfHeight = Math.Max(10, (int)Math.Round(dot.Bounds.Height * 0.85));
            int top = Math.Max(0, dot.Center.Y - halfHeight);
            int bottom = Math.Min(list.Height, dot.Center.Y + halfHeight + 1);
            int width = Math.Min(list.Width - left, Math.Max(40, (int)Math.Round(list.Width * 0.65)));
            using (Bitmap title = list.Clone(new Rectangle(left, top, width, bottom - top), PixelFormat.Format24bppRgb))
            {
                BitmapData data = title.LockBits(new Rectangle(Point.Empty, title.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                byte[] pixels;
                try { pixels = new byte[data.Stride * title.Height]; Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
                finally { title.UnlockBits(data); }
                byte[] ink = new byte[title.Width * title.Height];
                int x1 = title.Width, y1 = title.Height, x2 = -1, y2 = -1;
                for (int y = 0; y < title.Height; y++)
                    for (int x = 0; x < title.Width; x++)
                    {
                        int offset = y * data.Stride + x * 3;
                        if (pixels[offset + 2] * 0.299 + pixels[offset + 1] * 0.587 + pixels[offset] * 0.114 >= 235) continue;
                        ink[y * title.Width + x] = 1;
                        x1 = Math.Min(x1, x); y1 = Math.Min(y1, y); x2 = Math.Max(x2, x); y2 = Math.Max(y2, y);
                    }
                if (x2 < x1 || y2 < y1) return null;
                int inkWidth = x2 - x1 + 1, inkHeight = y2 - y1 + 1;
                // A tiny neighboring decoration (for example an assistant's other shoe)
                // is not a course title and must not create a second status column.
                if (requireTitle && inkWidth < dot.Bounds.Width * 2) return null;
                byte[] signature = new byte[8 + inkWidth * inkHeight];
                Buffer.BlockCopy(BitConverter.GetBytes(inkWidth), 0, signature, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(inkHeight), 0, signature, 4, 4);
                for (int y = 0; y < inkHeight; y++) Buffer.BlockCopy(ink, (y1 + y) * title.Width + x1, signature, 8 + y * inkWidth, inkWidth);
                using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(signature));
            }
        }

        // Compare only the actual RGB pixels in the video crop, never PNG metadata,
        // the task point, the side directory, or row padding bytes.
        public static bool SameFrame(Bitmap first, Bitmap second)
        {
            if (first == null || second == null || first.Size != second.Size) return false;
            Rectangle rectangle = new Rectangle(Point.Empty, first.Size);
            BitmapData a = first.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                BitmapData b = second.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] rowA = new byte[first.Width * 3], rowB = new byte[first.Width * 3];
                    for (int y = 0; y < first.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), rowA, 0, rowA.Length);
                        Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), rowB, 0, rowB.Length);
                        for (int x = 0; x < rowA.Length; x++) if (rowA[x] != rowB[x]) return false;
                    }
                    return true;
                }
                finally { second.UnlockBits(b); }
            }
            finally { first.UnlockBits(a); }
        }

        public static bool SamePage(Bitmap a, Bitmap b)
        {
            if (a == null || b == null || a.Size != b.Size) return false;
            long difference = 0;
            int samples = 0;
            for (int y = 3; y < a.Height; y += Math.Max(4, a.Height / 65))
                for (int x = 3; x < a.Width; x += Math.Max(4, a.Width / 85))
                {
                    Color first = a.GetPixel(x, y), second = b.GetPixel(x, y);
                    difference += Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);
                    samples++;
                }
            return samples > 0 && (double)difference / (samples * 3) < 1.8;
        }
    }
}
